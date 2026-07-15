// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace WinApp.Cli.Helpers;

internal static partial class KeyboardInput
{
    private const ushort VkMenu = 0x12;

    private readonly record struct PostedKey(ushort VirtualKey, bool Extended);

    private static void SendViaPostMessage(HWND hwnd, IReadOnlyList<KeyAction> actions, HKL keyboardLayout)
    {
        var heldKeys = new List<PostedKey>();
        try
        {
            foreach (var action in actions)
            {
                switch (action)
                {
                    case KeyChord chord:
                        PostChord(hwnd, chord, keyboardLayout, heldKeys);
                        break;

                    case TextInput text:
                        foreach (var ch in text.Text)
                        {
                            PostMessageChecked(
                                hwnd,
                                PInvoke.WM_CHAR,
                                new WPARAM(ch),
                                new LPARAM(1),
                                "WM_CHAR");
                        }
                        break;
                }

                Thread.Sleep(5);
            }
        }
        catch (KeyboardInjectionException ex)
        {
            var cleanupFailures = ReleasePostedKeys(hwnd, keyboardLayout, heldKeys);
            if (cleanupFailures > 0)
            {
                throw new KeyboardInjectionException(
                    ex.Code,
                    $"{ex.Message} Best-effort PostMessage key-up cleanup also failed for " +
                    $"{cleanupFailures} key(s); the target may have observed a partial sequence.",
                    ex);
            }

            throw;
        }
    }

    private static void PostChord(
        HWND hwnd,
        KeyChord chord,
        HKL keyboardLayout,
        List<PostedKey> heldKeys)
    {
        int altDownCount = 0;

        foreach (var modifier in chord.Modifiers)
        {
            PostKeyDownChecked(hwnd, keyboardLayout, modifier, IsExtended(modifier), ref altDownCount);
            heldKeys.Add(new PostedKey(modifier, IsExtended(modifier)));
        }

        var mainVirtualKey = ResolveChordVirtualKey(chord, keyboardLayout);
        PostKeyDownChecked(hwnd, keyboardLayout, mainVirtualKey, chord.Extended, ref altDownCount);
        heldKeys.Add(new PostedKey(mainVirtualKey, chord.Extended));

        PostKeyUpChecked(hwnd, keyboardLayout, mainVirtualKey, chord.Extended, ref altDownCount);
        heldKeys.RemoveAt(heldKeys.Count - 1);

        for (int i = chord.Modifiers.Count - 1; i >= 0; i--)
        {
            var modifier = chord.Modifiers[i];
            PostKeyUpChecked(hwnd, keyboardLayout, modifier, IsExtended(modifier), ref altDownCount);
            heldKeys.RemoveAt(heldKeys.Count - 1);
        }
    }

    private static int ReleasePostedKeys(HWND hwnd, HKL keyboardLayout, List<PostedKey> heldKeys)
    {
        int failures = 0;
        int altDownCount = heldKeys.Count(key => IsAltKey(key.VirtualKey));
        for (int i = heldKeys.Count - 1; i >= 0; i--)
        {
            var key = heldKeys[i];
            bool isAlt = IsAltKey(key.VirtualKey);
            bool posted = TryPostKey(
                    hwnd,
                    keyboardLayout,
                    isSystem: altDownCount > 0 || isAlt,
                    altContext: altDownCount > 0,
                    keyUp: true,
                    key.VirtualKey,
                    key.Extended,
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

        heldKeys.Clear();
        return failures;
    }

    private static void PostKeyDownChecked(
        HWND hwnd,
        HKL keyboardLayout,
        ushort virtualKey,
        bool extended,
        ref int altDownCount)
    {
        bool isAlt = IsAltKey(virtualKey);
        PostKeyChecked(
            hwnd,
            keyboardLayout,
            isSystem: altDownCount > 0 || isAlt,
            altContext: altDownCount > 0,
            keyUp: false,
            virtualKey,
            extended);
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
        ref int altDownCount)
    {
        bool isAlt = IsAltKey(virtualKey);
        PostKeyChecked(
            hwnd,
            keyboardLayout,
            isSystem: altDownCount > 0 || isAlt,
            altContext: altDownCount > 0,
            keyUp: true,
            virtualKey,
            extended);
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
        bool extended)
    {
        if (!TryPostKey(
                hwnd,
                keyboardLayout,
                isSystem,
                altContext,
                keyUp,
                virtualKey,
                extended,
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

        return TryPostMessage(
            hwnd,
            message,
            new WPARAM(virtualKey),
            new LPARAM((nint)(int)lParam),
            out error);
    }

    private static void PostMessageChecked(
        HWND hwnd,
        uint message,
        WPARAM wParam,
        LPARAM lParam,
        string operation)
    {
        if (TryPostMessage(hwnd, message, wParam, lParam, out var error))
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

    private static bool IsAltKey(ushort virtualKey) => virtualKey is VkMenu or 0xA4 or 0xA5;
}
