// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Synthesizes keyboard input via either PostMessage (HWND-targeted) or SendInput (OS-wide).
/// </summary>
/// <remarks>
/// Both transports are subject to UIPI and can target only equal- or lower-integrity processes.
/// PostMessage is window-queue scoped and does not trigger low-level hooks or global hotkeys;
/// SendInput changes global input state and therefore requires a verified foreground target.
/// </remarks>
internal static partial class KeyboardInput
{
    private const ushort VkShift = 0x10;
    private const ushort VkL = 0x4C;
    private const ushort VkLWin = 0x5B;
    private const ushort VkRWin = 0x5C;

    public static void Send(long hwnd, IReadOnlyList<KeyAction> actions, KeyTransport transport)
    {
        if (hwnd == 0)
        {
            throw new KeyboardInjectionException(
                UiJsonError.CodeNoTargetWindow,
                "Keyboard input requires a resolvable target window. Pass --window/--app or --target.");
        }

        var target = new HWND((nint)hwnd);
        if (!PInvoke.IsWindow(target))
        {
            throw new KeyboardInjectionException(
                UiJsonError.CodeNoTargetWindow,
                $"Keyboard input target 0x{hwnd:X} is not a live window.");
        }

        var keyboardLayout = GetTargetKeyboardLayout(target);

        switch (transport)
        {
            case KeyTransport.PostMessage:
                SendViaPostMessage(target, actions, keyboardLayout);
                break;
            case KeyTransport.SendInput:
                SendViaSendInput(hwnd, actions, keyboardLayout);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unknown key transport.");
        }
    }

    private static unsafe void SendViaSendInput(
        long targetHwnd,
        IReadOnlyList<KeyAction> actions,
        HKL keyboardLayout)
    {
        var inputs = new List<INPUT>();

        foreach (var action in actions)
        {
            switch (action)
            {
                case KeyChord chord:
                    foreach (var modifier in chord.Modifiers)
                    {
                        inputs.Add(KeyEvent(modifier, IsExtended(modifier), keyUp: false));
                    }

                    var mainVirtualKey = ResolveChordVirtualKey(chord, keyboardLayout);
                    inputs.Add(KeyEvent(mainVirtualKey, chord.Extended, keyUp: false));
                    inputs.Add(KeyEvent(mainVirtualKey, chord.Extended, keyUp: true));

                    for (int i = chord.Modifiers.Count - 1; i >= 0; i--)
                    {
                        var modifier = chord.Modifiers[i];
                        inputs.Add(KeyEvent(modifier, IsExtended(modifier), keyUp: true));
                    }
                    break;

                case TextInput text:
                    foreach (var ch in text.Text)
                    {
                        AppendCharEvents(inputs, ch, keyboardLayout);
                    }
                    break;
            }
        }

        if (inputs.Count == 0)
        {
            return;
        }

        var batch = inputs.ToArray();

        // Re-check at the last practical boundary. This narrows, but cannot eliminate, the foreground
        // TOCTOU window between verification and the SendInput syscall.
        EnsureFinalForeground(targetHwnd);
        EnsureNoWinLRisk(batch, actions);

        fixed (INPUT* pointer = batch)
        {
            var sent = PInvoke.SendInput((uint)batch.Length, pointer, sizeof(INPUT));
            if (sent == (uint)batch.Length)
            {
                return;
            }

            var releases = BuildReleaseInputs(batch, sent);
            uint released = 0;
            if (releases.Length > 0)
            {
                fixed (INPUT* releasePointer = releases)
                {
                    released = PInvoke.SendInput((uint)releases.Length, releasePointer, sizeof(INPUT));
                }
            }

            var cleanup = DescribeSendInputCleanup(
                sent,
                (uint)batch.Length,
                releases.Length,
                released);

            if (PInvoke.GetForegroundWindow().IsNull)
            {
                throw new KeyboardInjectionException(
                    UiJsonError.CodeNoInteractiveDesktop,
                    $"SendInput delivered {sent} of {batch.Length} key events because no interactive desktop is " +
                    $"available (the session may be locked or on a secure desktop). {cleanup}");
            }

            var reason = sent == 0
                ? "SendInput delivered no events. It can inject only into equal- or lower-integrity targets; " +
                  "Windows does not identify UIPI blocking with a distinct error."
                : $"SendInput delivered only {sent} of {batch.Length} key events; input was partially applied.";

            throw new KeyboardInjectionException(
                UiJsonError.CodeInputInjectionFailed,
                $"{reason} {cleanup}");
        }
    }

    private static void EnsureFinalForeground(long targetHwnd)
    {
        if (ForegroundGuard.ForegroundBelongsTo(targetHwnd))
        {
            return;
        }

        if (ForegroundGuard.NoInteractiveDesktop())
        {
            throw new KeyboardInjectionException(
                UiJsonError.CodeNoInteractiveDesktop,
                "Refusing SendInput because no interactive desktop is available. Unlock the session and retry.");
        }

        throw new KeyboardInjectionException(
            UiJsonError.CodeForegroundNotTarget,
            "Refusing SendInput because the requested target is no longer the foreground window.");
    }

    private static void EnsureNoWinLRisk(INPUT[] batch, IReadOnlyList<KeyAction> actions)
    {
        bool leftWinDown = IsKeyDown(VkLWin);
        bool rightWinDown = IsKeyDown(VkRWin);
        bool lDown = IsKeyDown(VkL);

        bool semanticRisk = (leftWinDown || rightWinDown) && SystemKeyGuard.ContainsSemanticL(actions);
        bool transitionRisk = SystemKeyGuard.WouldCreateWinL(
            GetVirtualKeyTransitions(batch),
            leftWinDown,
            rightWinDown,
            lDown);

        if (semanticRisk || transitionRisk)
        {
            throw new KeyboardInjectionException(
                UiJsonError.CodeInvalidArguments,
                "Refusing SendInput because the current keyboard state or requested batch could form Win+L. " +
                "Release both Windows keys and L, then retry; Win+L is never bypassable.");
        }
    }

    private static IEnumerable<SystemKeyGuard.VirtualKeyTransition> GetVirtualKeyTransitions(INPUT[] batch)
    {
        foreach (var input in batch)
        {
            if (input.type != INPUT_TYPE.INPUT_KEYBOARD || input.Anonymous.ki.wVk == 0)
            {
                continue;
            }

            yield return new SystemKeyGuard.VirtualKeyTransition(
                (ushort)input.Anonymous.ki.wVk,
                (input.Anonymous.ki.dwFlags & KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP) != 0);
        }
    }

    private static bool IsKeyDown(ushort virtualKey)
        => (PInvoke.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// Builds key-up events only for keys still held after replaying the prefix Windows reports as
    /// delivered. A zero-length prefix therefore never releases a physical key the CLI did not inject.
    /// </summary>
    internal static INPUT[] BuildReleaseInputs(INPUT[] attemptedBatch, uint deliveredCount)
    {
        var held = new List<KEYBDINPUT>();
        int count = (int)Math.Min(deliveredCount, (uint)attemptedBatch.Length);

        for (int i = 0; i < count; i++)
        {
            var input = attemptedBatch[i];
            if (input.type != INPUT_TYPE.INPUT_KEYBOARD)
            {
                continue;
            }

            var key = input.Anonymous.ki;
            if ((key.dwFlags & KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP) == 0)
            {
                held.Add(key);
                continue;
            }

            int matchingDown = held.FindLastIndex(candidate => SameKey(candidate, key));
            if (matchingDown >= 0)
            {
                held.RemoveAt(matchingDown);
            }
        }

        var releases = new INPUT[held.Count];
        for (int i = 0; i < held.Count; i++)
        {
            var key = held[held.Count - 1 - i];
            key.dwFlags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
            releases[i] = new INPUT
            {
                type = INPUT_TYPE.INPUT_KEYBOARD,
                Anonymous = { ki = key }
            };
        }

        return releases;
    }

    internal static string DescribeSendInputCleanup(
        uint deliveredCount,
        uint attemptedCount,
        int releaseCount,
        uint releasedCount)
    {
        if (releaseCount == 0)
        {
            return deliveredCount == 0
                ? "No key events were inserted, so no synthetic key-up was needed; retrying the full gesture is safe."
                : $"The delivered prefix inserted {deliveredCount} of {attemptedCount} key events and left no " +
                  "injected keys held down. Those events were already applied; do not blindly retry the full gesture.";
        }

        return releasedCount == (uint)releaseCount
            ? $"Released all {releaseCount} key(s) still held by the delivered prefix."
            : $"Key-up cleanup delivered only {releasedCount} of {releaseCount} release event(s).";
    }

    private static bool SameKey(KEYBDINPUT left, KEYBDINPUT right)
    {
        const KEYBD_EVENT_FLAGS identityFlags =
            KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY |
            KEYBD_EVENT_FLAGS.KEYEVENTF_SCANCODE |
            KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE;

        return left.wVk == right.wVk &&
               left.wScan == right.wScan &&
               (left.dwFlags & identityFlags) == (right.dwFlags & identityFlags);
    }

    internal static INPUT KeyEvent(ushort virtualKey, bool extended, bool keyUp)
    {
        var flags = (KEYBD_EVENT_FLAGS)0;
        if (keyUp) { flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP; }
        if (extended) { flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY; }

        return new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = { ki = new KEYBDINPUT { wVk = (VIRTUAL_KEY)virtualKey, dwFlags = flags } }
        };
    }

    private static INPUT UnicodeEvent(char ch, bool keyUp)
    {
        var flags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE;
        if (keyUp) { flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP; }

        return new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = flags } }
        };
    }

    private static void AppendCharEvents(List<INPUT> inputs, char ch, HKL keyboardLayout)
    {
        short scan = PInvoke.VkKeyScanEx(ch, keyboardLayout);
        int low = scan & 0xFF;
        int high = (scan >> 8) & 0xFF;

        bool mappable = scan != -1 && low != 0xFF;
        bool needsControlOrAlt = (high & 0x02) != 0 || (high & 0x04) != 0;

        if (!mappable || needsControlOrAlt)
        {
            inputs.Add(UnicodeEvent(ch, keyUp: false));
            inputs.Add(UnicodeEvent(ch, keyUp: true));
            return;
        }

        var virtualKey = (ushort)low;
        bool needsShift = (high & 0x01) != 0;

        if (needsShift) { inputs.Add(KeyEvent(VkShift, extended: false, keyUp: false)); }
        inputs.Add(KeyEvent(virtualKey, extended: false, keyUp: false));
        inputs.Add(KeyEvent(virtualKey, extended: false, keyUp: true));
        if (needsShift) { inputs.Add(KeyEvent(VkShift, extended: false, keyUp: true)); }
    }

    private static ushort ResolveChordVirtualKey(KeyChord chord, HKL keyboardLayout)
    {
        if (chord.SemanticKey is { Length: 1 })
        {
            short mapped = PInvoke.VkKeyScanEx(chord.SemanticKey[0], keyboardLayout);
            int low = mapped & 0xFF;
            if (mapped != -1 && low != 0xFF)
            {
                return (ushort)low;
            }
        }

        return chord.Vk;
    }

    private static unsafe HKL GetTargetKeyboardLayout(HWND hwnd)
    {
        uint processId = 0;
        uint threadId = PInvoke.GetWindowThreadProcessId(hwnd, &processId);
        return PInvoke.GetKeyboardLayout(threadId);
    }

    private static bool IsExtended(ushort virtualKey)
        => virtualKey is 0x5B or 0x5C or 0x5D or 0xA3 or 0xA5;
}
