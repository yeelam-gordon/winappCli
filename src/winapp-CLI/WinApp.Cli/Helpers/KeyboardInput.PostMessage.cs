// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace WinApp.Cli.Helpers;

internal static partial class KeyboardInput
{
    private readonly record struct PostedKey(ushort VirtualKey, bool Extended);
    private readonly record struct PostedKeyCleanup(int Failures, Exception? Error);

    internal delegate bool PostMessageInvoker(
        HWND hwnd,
        uint message,
        WPARAM wParam,
        LPARAM lParam,
        out int error);

    private static void SendViaPostMessage(HWND hwnd, IReadOnlyList<KeyAction> actions, HKL keyboardLayout)
        => SendViaPostMessageCore(hwnd, actions, keyboardLayout, TryPostMessage, PInvoke.VkKeyScanEx);

    internal static void SendViaPostMessage(
        HWND hwnd,
        IReadOnlyList<KeyAction> actions,
        HKL keyboardLayout,
        PostMessageInvoker postMessage)
        => SendViaPostMessageCore(hwnd, actions, keyboardLayout, postMessage, PInvoke.VkKeyScanEx);

    internal static void SendViaPostMessage(
        HWND hwnd,
        IReadOnlyList<KeyAction> actions,
        HKL keyboardLayout,
        PostMessageInvoker postMessage,
        KeyScanMapper mapCharacter)
        => SendViaPostMessageCore(hwnd, actions, keyboardLayout, postMessage, mapCharacter);

    private static void SendViaPostMessageCore(
        HWND hwnd,
        IReadOnlyList<KeyAction> actions,
        HKL keyboardLayout,
        PostMessageInvoker postMessage,
        KeyScanMapper mapCharacter)
    {
        var resolvedChords = actions
            .Select(action => action is KeyChord chord
                ? ResolveChord(chord, keyboardLayout, mapCharacter)
                : (ResolvedKeyChord?)null)
            .ToArray();
        var heldKeys = new List<PostedKey>();
        try
        {
            for (int actionIndex = 0; actionIndex < actions.Count; actionIndex++)
            {
                var action = actions[actionIndex];
                switch (action)
                {
                    case KeyChord:
                        PostChord(
                            hwnd,
                            resolvedChords[actionIndex]!.Value,
                            keyboardLayout,
                            heldKeys,
                            postMessage);
                        break;

                    case TextInput text:
                        foreach (var ch in text.Text)
                        {
                            PostMessageChecked(
                                hwnd,
                                PInvoke.WM_CHAR,
                                new WPARAM(ch),
                                new LPARAM(1),
                                "WM_CHAR",
                                postMessage);
                        }
                        break;
                }

                Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            var cleanup = TryReleasePostedKeys(hwnd, keyboardLayout, heldKeys, postMessage);
            if (cleanup.Failures > 0)
            {
                var cleanupDetail = cleanup.Error is null
                    ? $"failed for {cleanup.Failures} key(s)"
                    : $"failed for {cleanup.Failures} key(s), including " +
                      $"{cleanup.Error.GetType().Name} during cleanup";
                Exception innerException = cleanup.Error is null
                    ? ex
                    : new AggregateException(ex, cleanup.Error);

                throw new KeyboardInjectionException(
                    ex is KeyboardInjectionException injectionEx
                        ? injectionEx.Code
                        : UiJsonError.CodeInputInjectionFailed,
                    $"{ex.Message} Best-effort PostMessage key-up cleanup also {cleanupDetail}; " +
                    "the target may have observed a partial sequence.",
                    innerException);
            }

            throw;
        }
    }

    private static void PostChord(
        HWND hwnd,
        ResolvedKeyChord chord,
        HKL keyboardLayout,
        List<PostedKey> heldKeys,
        PostMessageInvoker postMessage)
    {
        int altDownCount = 0;

        foreach (var modifier in chord.Modifiers)
        {
            PostKeyDownChecked(hwnd, keyboardLayout, modifier, IsExtended(modifier), ref altDownCount, postMessage);
            heldKeys.Add(new PostedKey(modifier, IsExtended(modifier)));
        }

        PostKeyDownChecked(
            hwnd,
            keyboardLayout,
            chord.VirtualKey,
            chord.Extended,
            ref altDownCount,
            postMessage);
        heldKeys.Add(new PostedKey(chord.VirtualKey, chord.Extended));

        PostKeyUpChecked(
            hwnd,
            keyboardLayout,
            chord.VirtualKey,
            chord.Extended,
            ref altDownCount,
            postMessage);
        heldKeys.RemoveAt(heldKeys.Count - 1);

        for (int i = chord.Modifiers.Count - 1; i >= 0; i--)
        {
            var modifier = chord.Modifiers[i];
            PostKeyUpChecked(hwnd, keyboardLayout, modifier, IsExtended(modifier), ref altDownCount, postMessage);
            heldKeys.RemoveAt(heldKeys.Count - 1);
        }
    }

    private static PostedKeyCleanup TryReleasePostedKeys(
        HWND hwnd,
        HKL keyboardLayout,
        List<PostedKey> heldKeys,
        PostMessageInvoker postMessage)
    {
        int trackedKeyCount = heldKeys.Count;
        try
        {
            return ReleasePostedKeys(hwnd, keyboardLayout, heldKeys, postMessage);
        }
        catch (Exception cleanupError)
        {
            heldKeys.Clear();
            return new PostedKeyCleanup(Math.Max(1, trackedKeyCount), cleanupError);
        }
    }

    private static PostedKeyCleanup ReleasePostedKeys(
        HWND hwnd,
        HKL keyboardLayout,
        List<PostedKey> heldKeys,
        PostMessageInvoker postMessage)
    {
        int failures = 0;
        Exception? firstError = null;
        int altDownCount = heldKeys.Count(key => IsAltKey(key.VirtualKey));
        for (int i = heldKeys.Count - 1; i >= 0; i--)
        {
            var key = heldKeys[i];
            bool isAlt = IsAltKey(key.VirtualKey);
            try
            {
                bool posted = TryPostKey(
                    hwnd,
                    keyboardLayout,
                    isSystem: altDownCount > 0 || isAlt,
                    altContext: altDownCount > 0,
                    keyUp: true,
                    key.VirtualKey,
                    key.Extended,
                    postMessage,
                    out _);
                if (!posted)
                {
                    failures++;
                }
                else if (isAlt)
                {
                    altDownCount--;
                }
            }
            catch (Exception cleanupError)
            {
                failures++;
                firstError ??= cleanupError;
            }
        }

        heldKeys.Clear();
        return new PostedKeyCleanup(failures, firstError);
    }

    private static void PostKeyDownChecked(
        HWND hwnd,
        HKL keyboardLayout,
        ushort virtualKey,
        bool extended,
        ref int altDownCount,
        PostMessageInvoker postMessage)
    {
        bool isAlt = IsAltKey(virtualKey);
        PostKeyChecked(
            hwnd,
            keyboardLayout,
            isSystem: altDownCount > 0 || isAlt,
            altContext: altDownCount > 0,
            keyUp: false,
            virtualKey,
            extended,
            postMessage);
        if (isAlt)
        {
            altDownCount++;
        }
    }

    private static void PostKeyUpChecked(
        HWND hwnd,
        HKL keyboardLayout,
        ushort virtualKey,
        bool extended,
        ref int altDownCount,
        PostMessageInvoker postMessage)
    {
        bool isAlt = IsAltKey(virtualKey);
        PostKeyChecked(
            hwnd,
            keyboardLayout,
            isSystem: altDownCount > 0 || isAlt,
            altContext: altDownCount > 0,
            keyUp: true,
            virtualKey,
            extended,
            postMessage);
        if (isAlt && altDownCount > 0)
        {
            altDownCount--;
        }
    }

    private static void PostKeyChecked(
        HWND hwnd,
        HKL keyboardLayout,
        bool isSystem,
        bool altContext,
        bool keyUp,
        ushort virtualKey,
        bool extended,
        PostMessageInvoker postMessage)
    {
        if (!TryPostKey(
                hwnd,
                keyboardLayout,
                isSystem,
                altContext,
                keyUp,
                virtualKey,
                extended,
                postMessage,
                out var error))
        {
            ThrowPostMessageFailure(error, keyUp ? "key-up" : "key-down");
        }
    }

    private static bool TryPostKey(
        HWND hwnd,
        HKL keyboardLayout,
        bool isSystem,
        bool altContext,
        bool keyUp,
        ushort virtualKey,
        bool extended,
        PostMessageInvoker postMessage,
        out int error)
    {
        uint message = isSystem
            ? (keyUp ? PInvoke.WM_SYSKEYUP : PInvoke.WM_SYSKEYDOWN)
            : (keyUp ? PInvoke.WM_KEYUP : PInvoke.WM_KEYDOWN);

        uint scan = PInvoke.MapVirtualKeyEx(
            virtualKey,
            MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC,
            keyboardLayout);

        uint lParam = 1u
            | (scan << 16)
            | (extended ? 1u << 24 : 0u)
            | (altContext ? 1u << 29 : 0u);

        if (keyUp)
        {
            lParam |= (1u << 30) | (1u << 31);
        }

        return postMessage(
            hwnd,
            message,
            new WPARAM(NormalizePostedVirtualKey(virtualKey)),
            new LPARAM((nint)(int)lParam),
            out error);
    }

    private static void PostMessageChecked(
        HWND hwnd,
        uint message,
        WPARAM wParam,
        LPARAM lParam,
        string operation,
        PostMessageInvoker postMessage)
    {
        if (postMessage(hwnd, message, wParam, lParam, out var error))
        {
            return;
        }

        ThrowPostMessageFailure(error, operation);
    }

    private static bool TryPostMessage(
        HWND hwnd,
        uint message,
        WPARAM wParam,
        LPARAM lParam,
        out int error)
    {
        Marshal.SetLastPInvokeError(0);
        if (PInvoke.PostMessage(hwnd, message, wParam, lParam))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        return false;
    }

    private static void ThrowPostMessageFailure(int error, string operation)
        => throw CreatePostMessageException(error, operation);

    internal static KeyboardInjectionException CreatePostMessageException(int error, string operation)
    {
        var detail = error == 5
            ? "Access was denied by UIPI; PostMessage can target only an equal- or lower-integrity process."
            : error == 0
                ? "Windows returned no extended error."
                : $"Win32 error {error}.";

        return new KeyboardInjectionException(
            UiJsonError.CodeInputInjectionFailed,
            $"PostMessage failed while posting {operation}. {detail} The target may have observed a partial sequence.");
    }

    internal static ushort NormalizePostedVirtualKey(ushort virtualKey)
        => virtualKey switch
        {
            0xA0 or 0xA1 => VkShift,
            0xA2 or 0xA3 => VkControl,
            0xA4 or 0xA5 => VkMenu,
            _ => virtualKey
        };

    private static bool IsAltKey(ushort virtualKey) => virtualKey is VkMenu or 0xA4 or 0xA5;
}
