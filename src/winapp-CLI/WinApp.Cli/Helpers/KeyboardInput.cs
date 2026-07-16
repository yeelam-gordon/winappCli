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
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkL = 0x4C;
    private const ushort VkLWin = 0x5B;
    private const ushort VkRWin = 0x5C;

    internal readonly record struct ResolvedKeyChord(
        IReadOnlyList<ushort> Modifiers,
        ushort VirtualKey,
        bool Extended);

    internal delegate short KeyScanMapper(char character, HKL keyboardLayout);
    internal delegate uint SendInputInvoker(INPUT[] inputs);
    internal delegate void ForegroundValidator(long targetHwnd);
    internal delegate bool KeyStateProbe(ushort virtualKey);

    private readonly record struct InputActionBatch(KeyAction Action, INPUT[] Inputs);

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

    private static void SendViaSendInput(
        long targetHwnd,
        IReadOnlyList<KeyAction> actions,
        HKL keyboardLayout)
        => SendViaSendInputCore(
            targetHwnd,
            actions,
            keyboardLayout,
            InvokeSendInput,
            EnsureFinalForeground,
            IsKeyDown,
            PInvoke.VkKeyScanEx);

    internal static void SendViaSendInput(
        long targetHwnd,
        IReadOnlyList<KeyAction> actions,
        HKL keyboardLayout,
        SendInputInvoker sendInput,
        ForegroundValidator ensureForeground,
        KeyStateProbe isKeyDown,
        KeyScanMapper mapCharacter)
        => SendViaSendInputCore(
            targetHwnd,
            actions,
            keyboardLayout,
            sendInput,
            ensureForeground,
            isKeyDown,
            mapCharacter);

    private static void SendViaSendInputCore(
        long targetHwnd,
        IReadOnlyList<KeyAction> actions,
        HKL keyboardLayout,
        SendInputInvoker sendInput,
        ForegroundValidator ensureForeground,
        KeyStateProbe isKeyDown,
        KeyScanMapper mapCharacter)
    {
        EnsureSystemShortcutsAreIsolated(actions);

        // Resolve every action before injecting the first one. A target-layout mapping failure must
        // fail closed without applying an earlier prefix of the requested sequence.
        var batches = actions
            .Select(action => new InputActionBatch(
                action,
                BuildActionInputs(action, keyboardLayout, mapCharacter)))
            .Where(batch => batch.Inputs.Length > 0)
            .ToArray();

        foreach (var actionBatch in batches)
        {
            var batch = actionBatch.Inputs;

            // Re-check before every semantic action so a preceding action cannot redirect the
            // remainder of the request to a different foreground process.
            ensureForeground(targetHwnd);
            EnsureNoWinLRisk(batch, [actionBatch.Action], isKeyDown);

            var initiallyDownKeys = FindInitiallyDownConflictingKeys(batch, isKeyDown);
            if (initiallyDownKeys.Count > 0)
            {
                var keys = string.Join(", ", initiallyDownKeys.Select(key => $"0x{key:X2}"));
                throw new KeyboardInjectionException(
                    UiJsonError.CodeInputInjectionFailed,
                    $"Refusing SendInput because a requested key or modifier is already physically held: {keys}. " +
                    "Release them and retry so synthetic cleanup cannot release user-owned key state.");
            }

            var sent = sendInput(batch);
            if (sent == (uint)batch.Length)
            {
                continue;
            }

            var releases = BuildReleaseInputs(batch, sent, initiallyDownKeys.ToHashSet());
            uint released = releases.Length > 0 ? sendInput(releases) : 0;
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

    private static INPUT[] BuildActionInputs(
        KeyAction action,
        HKL keyboardLayout,
        KeyScanMapper mapCharacter)
    {
        var inputs = new List<INPUT>();
        switch (action)
        {
            case KeyChord chord:
                var resolvedChord = ResolveChord(chord, keyboardLayout, mapCharacter);
                foreach (var modifier in resolvedChord.Modifiers)
                {
                    inputs.Add(KeyEvent(modifier, IsExtended(modifier), keyUp: false));
                }

                inputs.Add(KeyEvent(resolvedChord.VirtualKey, resolvedChord.Extended, keyUp: false));
                inputs.Add(KeyEvent(resolvedChord.VirtualKey, resolvedChord.Extended, keyUp: true));

                for (int i = resolvedChord.Modifiers.Count - 1; i >= 0; i--)
                {
                    var modifier = resolvedChord.Modifiers[i];
                    inputs.Add(KeyEvent(modifier, IsExtended(modifier), keyUp: true));
                }
                break;

            case TextInput text:
                foreach (var ch in text.Text)
                {
                    AppendCharEvents(inputs, ch, keyboardLayout, mapCharacter);
                }
                break;
        }

        return inputs.ToArray();
    }

    private static unsafe uint InvokeSendInput(INPUT[] inputs)
    {
        fixed (INPUT* pointer = inputs)
        {
            return PInvoke.SendInput((uint)inputs.Length, pointer, sizeof(INPUT));
        }
    }

    internal static void EnsureSystemShortcutsAreIsolated(IReadOnlyList<KeyAction> actions)
    {
        var systemCombos = SystemKeyGuard.FindSystemCombos(actions);
        if (systemCombos.Count > 0 && actions.Count > 1)
        {
            throw new KeyboardInjectionException(
                UiJsonError.CodeInvalidArguments,
                $"System-reserved SendInput action(s) must be sent alone: {string.Join(", ", systemCombos)}. " +
                "A system shortcut can change foreground before later actions; send it in a separate command, " +
                "then resolve and foreground the next target again.");
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

    private static void EnsureNoWinLRisk(
        INPUT[] batch,
        IReadOnlyList<KeyAction> actions,
        KeyStateProbe isKeyDown)
    {
        bool leftWinDown = isKeyDown(VkLWin);
        bool rightWinDown = isKeyDown(VkRWin);
        bool lDown = isKeyDown(VkL);

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

    internal static IReadOnlyList<ushort> FindInitiallyDownConflictingKeys(
        INPUT[] batch,
        KeyStateProbe isKeyDown)
    {
        ushort[] ambientModifiers = [VkShift, VkControl, VkMenu, VkLWin, VkRWin];
        return batch
            .Where(input =>
                input.type == INPUT_TYPE.INPUT_KEYBOARD &&
                input.Anonymous.ki.wVk != 0 &&
                (input.Anonymous.ki.dwFlags & KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP) == 0)
            .Select(input => (ushort)input.Anonymous.ki.wVk)
            .Concat(ambientModifiers)
            .Distinct()
            .Where(isKeyDown.Invoke)
            .ToArray();
    }

    /// <summary>
    /// Builds key-up events only for keys still held after replaying the prefix Windows reports as
    /// delivered. A zero-length prefix therefore never releases a physical key the CLI did not inject.
    /// </summary>
    internal static INPUT[] BuildReleaseInputs(
        INPUT[] attemptedBatch,
        uint deliveredCount,
        IReadOnlySet<ushort>? initiallyDownVirtualKeys = null)
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
                if (key.wVk != 0 &&
                    initiallyDownVirtualKeys?.Contains((ushort)key.wVk) == true)
                {
                    continue;
                }

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

    private static void AppendCharEvents(
        List<INPUT> inputs,
        char ch,
        HKL keyboardLayout,
        KeyScanMapper mapCharacter)
    {
        short scan = mapCharacter(ch, keyboardLayout);
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
        bool extended = KeyStringParser.IsExtendedVk(virtualKey);

        if (needsShift) { inputs.Add(KeyEvent(VkShift, extended: false, keyUp: false)); }
        inputs.Add(KeyEvent(virtualKey, extended, keyUp: false));
        inputs.Add(KeyEvent(virtualKey, extended, keyUp: true));
        if (needsShift) { inputs.Add(KeyEvent(VkShift, extended: false, keyUp: true)); }
    }

    private static ResolvedKeyChord ResolveChord(KeyChord chord, HKL keyboardLayout)
        => ResolveChord(chord, keyboardLayout, PInvoke.VkKeyScanEx);

    internal static ResolvedKeyChord ResolveChord(
        KeyChord chord,
        HKL keyboardLayout,
        KeyScanMapper mapCharacter)
    {
        if (chord.SemanticKey is { Length: 1 })
        {
            char character = chord.SemanticKey[0];
            short mapped = mapCharacter(character, keyboardLayout);
            int virtualKey = mapped & 0xFF;
            int modifierState = (mapped >> 8) & 0xFF;
            if (mapped == -1 || virtualKey is 0 or 0xFF)
            {
                throw new KeyboardInjectionException(
                    UiJsonError.CodeInvalidArguments,
                    $"Character chord '{character}' cannot be mapped by the target window's keyboard layout. " +
                    "Use a named key or vk=0xNN for explicit physical-key semantics.");
            }

            const int supportedModifierBits = 0x01 | 0x02 | 0x04;
            if ((modifierState & ~supportedModifierBits) != 0)
            {
                throw new KeyboardInjectionException(
                    UiJsonError.CodeInvalidArguments,
                    $"Character chord '{character}' requires unsupported keyboard-layout modifier state " +
                    $"0x{modifierState:X2}. Use vk=0xNN for explicit physical-key semantics.");
            }

            var modifiers = chord.Modifiers.ToList();
            if ((modifierState & 0x01) != 0)
            {
                AddImplicitModifier(modifiers, VkShift, 0xA0, 0xA1, insertBeforeAlt: false);
            }
            if ((modifierState & 0x02) != 0)
            {
                AddImplicitModifier(modifiers, VkControl, 0xA2, 0xA3, insertBeforeAlt: true);
            }
            if ((modifierState & 0x04) != 0)
            {
                AddImplicitModifier(modifiers, VkMenu, 0xA4, 0xA5, insertBeforeAlt: false);
            }

            var resolvedVirtualKey = (ushort)virtualKey;
            return new ResolvedKeyChord(
                modifiers,
                resolvedVirtualKey,
                KeyStringParser.IsExtendedVk(resolvedVirtualKey));
        }

        return new ResolvedKeyChord(chord.Modifiers, chord.Vk, chord.Extended);
    }

    private static void AddImplicitModifier(
        List<ushort> modifiers,
        ushort generic,
        ushort left,
        ushort right,
        bool insertBeforeAlt)
    {
        if (!modifiers.Any(modifier => modifier == generic || modifier == left || modifier == right))
        {
            int insertionIndex = insertBeforeAlt
                ? modifiers.FindIndex(modifier => modifier is VkMenu or 0xA4 or 0xA5)
                : -1;
            if (insertionIndex >= 0)
            {
                modifiers.Insert(insertionIndex, generic);
            }
            else
            {
                modifiers.Add(generic);
            }
        }
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
