// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Recognizes system- and shell-reserved key combinations so <c>send-keys --via send-input</c> can reject
/// them by default, keep every Win-modified <c>L</c> chord permanently blocked, and require an explicit
/// opt-in for the remaining OS-wide combos. (<c>--via post-message</c> is window-scoped and is not affected.)
/// </summary>
internal static class SystemKeyGuard
{
    private const ushort VkShift = 0x10;
    private const ushort VkLShift = 0xA0;
    private const ushort VkRShift = 0xA1;
    private const ushort VkControl = 0x11;
    private const ushort VkLControl = 0xA2;
    private const ushort VkRControl = 0xA3;
    private const ushort VkAlt = 0x12;
    private const ushort VkLAlt = 0xA4;
    private const ushort VkRAlt = 0xA5;
    private const ushort VkEsc = 0x1B;
    private const ushort VkTab = 0x09;
    private const ushort VkDelete = 0x2E;
    private const ushort VkPrintScreen = 0x2C;
    private const ushort VkF4 = 0x73;
    private const ushort VkLWin = 0x5B;
    private const ushort VkRWin = 0x5C;
    private const ushort VkL = 0x4C; // 'L' — win+l triggers LockWorkStation() via the shell hook

    internal readonly record struct VirtualKeyTransition(ushort VirtualKey, bool IsKeyUp);

    /// <summary>
    /// Returns the friendly names of any combos that must NEVER be synthesized via send-input, even when
    /// the caller opts in with <c>--allow-system-keys</c>. Currently scoped to any chord combining
    /// VK_LWIN/VK_RWIN with either VK_L (0x4C) or a semantic <c>l</c> token, regardless of keyboard
    /// layout or additional modifiers. Windows lock handling may still recognize these variants and fire
    /// <c>LockWorkStation()</c>, locking the interactive session
    /// with no recovery path from automation. Unlike soft-blocked combos (<c>alt+f4</c>,
    /// <c>ctrl+shift+esc</c>, <c>win+r</c>, …) that can be opted into for driving global hotkeys, a
    /// possible session lock must fail closed because it halts unattended CI and remote-desktop automation
    /// until a user unlocks it.
    /// </summary>
    public static IReadOnlyList<string> FindNeverBypassableCombos(IEnumerable<KeyAction> actions)
    {
        var hits = new List<string>();
        foreach (var action in actions)
        {
            if (action is not KeyChord chord)
            {
                continue;
            }

            bool win = HasModifier(chord, VkLWin, VkRWin);
            bool isL = chord.Vk == VkL ||
                       string.Equals(chord.SemanticKey, "l", StringComparison.OrdinalIgnoreCase);
            if (win && isL)
            {
                const string name = "win+l";
                if (!hits.Contains(name))
                {
                    hits.Add(name);
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// Simulates Win/L state across a native key transition batch, including keys that were already
    /// physically held. This is a final defense against an otherwise-safe payload becoming Win+L.
    /// </summary>
    internal static bool WouldCreateWinL(
        IEnumerable<VirtualKeyTransition> transitions,
        bool leftWinDown,
        bool rightWinDown,
        bool lDown)
    {
        foreach (var transition in transitions)
        {
            switch (transition.VirtualKey)
            {
                case VkLWin:
                    leftWinDown = !transition.IsKeyUp;
                    break;
                case VkRWin:
                    rightWinDown = !transition.IsKeyUp;
                    break;
                case VkL:
                    lDown = !transition.IsKeyUp;
                    break;
            }

            if ((leftWinDown || rightWinDown) && lDown)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detects semantic L input that may be emitted as Unicode or as a layout-specific non-VK_L key.
    /// </summary>
    internal static bool ContainsSemanticL(IReadOnlyList<KeyAction> actions)
        => actions.Any(action => action switch
        {
            KeyChord chord => chord.Vk == VkL ||
                              string.Equals(chord.SemanticKey, "l", StringComparison.OrdinalIgnoreCase),
            TextInput text => text.Text.Any(character => char.ToLowerInvariant(character) == 'l'),
            _ => false,
        });

    /// <summary>
    /// Returns the friendly names of any system-reserved combos present in <paramref name="actions"/>,
    /// in first-seen order with duplicates removed. Empty when none are present.
    /// </summary>
    public static IReadOnlyList<string> FindSystemCombos(IEnumerable<KeyAction> actions)
    {
        var hits = new List<string>();
        foreach (var action in actions)
        {
            if (action is not KeyChord chord)
            {
                continue;
            }

            var name = Describe(chord);
            if (name is not null && !hits.Contains(name))
            {
                hits.Add(name);
            }
        }

        return hits;
    }

    private static string? Describe(KeyChord chord)
    {
        bool ctrl = HasModifier(chord, VkControl, VkLControl, VkRControl);
        bool shift = HasModifier(chord, VkShift, VkLShift, VkRShift);
        bool alt = HasModifier(chord, VkAlt, VkLAlt, VkRAlt);
        bool win = HasModifier(chord, VkLWin, VkRWin);

        // Any Win-modified combo is a shell shortcut (win+l lock, win+r Run, win+d desktop, win+e, …).
        if (win)
        {
            return "win+<key>";
        }

        if (chord.Modifiers.Count == 0)
        {
            // Lone Win key opens Start; lone PrintScreen captures the screen.
            if (chord.Vk is VkLWin or VkRWin)
            {
                return "win";
            }

            if (chord.Vk == VkPrintScreen)
            {
                return "printscreen";
            }

            return null;
        }

        // Alt+PrintScreen captures the active window to the clipboard.
        if (alt && chord.Vk == VkPrintScreen)
        {
            return "alt+printscreen";
        }

        if (ctrl && shift && chord.Vk == VkEsc)
        {
            return "ctrl+shift+esc";
        }

        if (ctrl && alt && chord.Vk == VkDelete)
        {
            return "ctrl+alt+del";
        }

        if (ctrl && !alt && !shift && chord.Vk == VkEsc)
        {
            return "ctrl+esc";
        }

        if (alt && chord.Vk == VkTab)
        {
            return "alt+tab";
        }

        if (alt && chord.Vk == VkEsc)
        {
            return "alt+esc";
        }

        if (alt && chord.Vk == VkF4)
        {
            return "alt+f4";
        }

        return null;
    }

    private static bool HasModifier(KeyChord chord, params ushort[] virtualKeys)
        => virtualKeys.Any(chord.Modifiers.Contains);
}
