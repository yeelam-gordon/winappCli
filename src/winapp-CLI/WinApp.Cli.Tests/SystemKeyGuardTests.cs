// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class SystemKeyGuardTests
{
    [TestMethod]
    [DataRow("win+l")]
    [DataRow("win+r")]
    [DataRow("win+d")]
    [DataRow("win+e")]
    [DataRow("cmd+l")]    // alias for win
    [DataRow("super+tab")]
    public void WinModifiedCombos_AreReportedGenerically(string keys)
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse(keys));
        Assert.AreEqual(1, combos.Count);
        Assert.AreEqual("win+<key>", combos[0]);
    }

    [TestMethod]
    [DataRow("ctrl+shift+esc", "ctrl+shift+esc")]
    [DataRow("ctrl+alt+del", "ctrl+alt+del")]
    [DataRow("ctrl+esc", "ctrl+esc")]
    [DataRow("alt+tab", "alt+tab")]
    [DataRow("alt+esc", "alt+esc")]
    [DataRow("alt+f4", "alt+f4")]
    [DataRow("alt+printscreen", "alt+printscreen")]
    [DataRow("lalt+f4", "alt+f4")]
    [DataRow("ralt+tab", "alt+tab")]
    [DataRow("rctrl+lalt+del", "ctrl+alt+del")]
    public void KnownSystemCombos_AreReportedByName(string keys, string expected)
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse(keys));
        Assert.AreEqual(1, combos.Count);
        Assert.AreEqual(expected, combos[0]);
    }

    [TestMethod]
    public void LoneWinKey_IsReported()
    {
        // The Win key has no named token; it can only be sent raw via vk=0x5B.
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("vk=0x5B"));
        Assert.AreEqual(1, combos.Count);
        Assert.AreEqual("win", combos[0]);
    }

    [TestMethod]
    public void LonePrintScreen_IsReported()
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("printscreen"));
        Assert.AreEqual(1, combos.Count);
        Assert.AreEqual("printscreen", combos[0]);
    }

    [TestMethod]
    [DataRow("ctrl+a")]
    [DataRow("ctrl+c")]
    [DataRow("ctrl+shift+t")]
    [DataRow("enter")]
    [DataRow("down down up")]
    [DataRow("f5")]
    [DataRow("alt+a")]
    [DataRow("ctrl+alt+a")]
    public void OrdinaryCombos_AreNotReported(string keys)
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse(keys));
        Assert.AreEqual(0, combos.Count);
    }

    [TestMethod]
    public void LiteralText_IsNotReported()
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("Hello world"));
        Assert.AreEqual(0, combos.Count);
    }

    [TestMethod]
    public void Duplicates_AreCollapsed()
    {
        // Two Win combos collapse to a single generic entry.
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("win+l win+r"));
        Assert.AreEqual(1, combos.Count);
        Assert.AreEqual("win+<key>", combos[0]);
    }

    [TestMethod]
    public void MultipleDistinctCombos_PreserveFirstSeenOrder()
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("alt+f4 ctrl+esc alt+tab"));
        Assert.AreEqual(3, combos.Count);
        Assert.AreEqual("alt+f4", combos[0]);
        Assert.AreEqual("ctrl+esc", combos[1]);
        Assert.AreEqual("alt+tab", combos[2]);
    }

    [TestMethod]
    public void MixedSafeAndSystemKeys_ReportsOnlySystem()
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("ctrl+a win+l enter"));
        Assert.AreEqual(1, combos.Count);
        Assert.AreEqual("win+<key>", combos[0]);
    }

    [TestMethod]
    public void EmptyActions_ReturnsEmpty()
    {
        Assert.AreEqual(0, SystemKeyGuard.FindSystemCombos([]).Count);
    }

    // --- FindNeverBypassableCombos ---

    [TestMethod]
    [DataRow("win+l")]   // standard win+l
    [DataRow("cmd+l")]   // cmd is an alias for win
    [DataRow("win+shift+l")]
    [DataRow("win+ctrl+l")]
    [DataRow("win+alt+l")]
    [DataRow("rwin+rshift+l")]
    [DataRow("lwin+rctrl+l")]
    [DataRow("rwin+lalt+l")]
    public void NeverBypassable_WinModifiedL_IsDetected(string keys)
    {
        // Fail closed for every Win-modified L chord because Windows lock handling may still recognize
        // variants with additional modifiers.
        var hits = SystemKeyGuard.FindNeverBypassableCombos(KeyStringParser.Parse(keys));
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual("win+l", hits[0]);
    }

    [TestMethod]
    public void NeverBypassable_RightWinModifiedL_IsDetected()
    {
        // The friendly grammar uses left Win for "win", so construct a right-Win + Shift + L chord
        // directly to verify that either Windows modifier preserves the hard block.
        KeyAction[] actions = [new KeyChord([0x5C, 0x10], 0x4C, Extended: false)];

        var hits = SystemKeyGuard.FindNeverBypassableCombos(actions);

        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual("win+l", hits[0]);
    }

    [TestMethod]
    public void NeverBypassable_SemanticL_IsDetectedWhenLayoutMapsDifferentVk()
    {
        KeyAction[] actions =
        [
            new KeyChord([0x5B], Vk: 0x4E, Extended: false, SemanticKey: "L")
        ];

        var hits = SystemKeyGuard.FindNeverBypassableCombos(actions);

        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual("win+l", hits[0]);
    }

    [TestMethod]
    public void NeverBypassable_RawVkL_IsDetectedWithoutSemanticKey()
    {
        KeyAction[] actions =
        [
            new KeyChord([0x5C], Vk: 0x4C, Extended: false)
        ];

        var hits = SystemKeyGuard.FindNeverBypassableCombos(actions);

        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual("win+l", hits[0]);
    }

    [TestMethod]
    public void NeverBypassable_LeftAndRightModifiers_CannotMaskEitherWinKey()
    {
        ushort[] winKeys = [0x5B, 0x5C];
        ushort[] extraModifiers = [0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];

        foreach (var winKey in winKeys)
        {
            foreach (var extraModifier in extraModifiers)
            {
                KeyAction[] actions =
                [
                    new KeyChord([winKey, extraModifier], Vk: 0x4E, Extended: false, SemanticKey: "l")
                ];

                Assert.AreEqual(
                    1,
                    SystemKeyGuard.FindNeverBypassableCombos(actions).Count,
                    $"Win key 0x{winKey:X2} plus modifier 0x{extraModifier:X2} must remain blocked.");
            }
        }
    }

    [TestMethod]
    public void WouldCreateWinL_DetectsAmbientLeftOrRightWin()
    {
        var lPress = new[] { new SystemKeyGuard.VirtualKeyTransition(0x4C, IsKeyUp: false) };

        Assert.IsTrue(SystemKeyGuard.WouldCreateWinL(lPress, leftWinDown: true, rightWinDown: false, lDown: false));
        Assert.IsTrue(SystemKeyGuard.WouldCreateWinL(lPress, leftWinDown: false, rightWinDown: true, lDown: false));
    }

    [TestMethod]
    public void WouldCreateWinL_DetectsAmbientLAndRequestedWin()
    {
        var rightWinPress = new[] { new SystemKeyGuard.VirtualKeyTransition(0x5C, IsKeyUp: false) };

        Assert.IsTrue(SystemKeyGuard.WouldCreateWinL(
            rightWinPress,
            leftWinDown: false,
            rightWinDown: false,
            lDown: true));
    }

    [TestMethod]
    public void WouldCreateWinL_DetectsRequestedRawTransitionPair()
    {
        SystemKeyGuard.VirtualKeyTransition[] transitions =
        [
            new(0x5B, IsKeyUp: false),
            new(0x4C, IsKeyUp: false),
        ];

        Assert.IsTrue(SystemKeyGuard.WouldCreateWinL(
            transitions,
            leftWinDown: false,
            rightWinDown: false,
            lDown: false));
    }

    [TestMethod]
    public void WouldCreateWinL_DoesNotFlagSequentialNonOverlappingKeys()
    {
        SystemKeyGuard.VirtualKeyTransition[] transitions =
        [
            new(0x5B, IsKeyUp: false),
            new(0x5B, IsKeyUp: true),
            new(0x4C, IsKeyUp: false),
            new(0x4C, IsKeyUp: true),
        ];

        Assert.IsFalse(SystemKeyGuard.WouldCreateWinL(
            transitions,
            leftWinDown: false,
            rightWinDown: false,
            lDown: false));
    }

    [TestMethod]
    public void ContainsSemanticL_CoversTextAndLayoutMappedChord()
    {
        Assert.IsTrue(SystemKeyGuard.ContainsSemanticL([new TextInput("hello")]));
        Assert.IsTrue(SystemKeyGuard.ContainsSemanticL(
            [new KeyChord([], Vk: 0x4E, Extended: false, SemanticKey: "l")]));
        Assert.IsFalse(SystemKeyGuard.ContainsSemanticL([new TextInput("queue")]));
    }

    [TestMethod]
    [DataRow("win+r")]
    [DataRow("win+d")]
    [DataRow("win+shift+v")]
    [DataRow("alt+f4")]
    [DataRow("ctrl+shift+esc")]
    [DataRow("vk=0x5B")]  // lone win key
    public void NeverBypassable_OtherCombos_AreNotHardBlocked(string keys)
    {
        // Soft-blocked non-L combos must NOT appear in the never-bypassable list — callers may
        // legitimately opt in to them with --allow-system-keys.
        var hits = SystemKeyGuard.FindNeverBypassableCombos(KeyStringParser.Parse(keys));
        Assert.AreEqual(0, hits.Count);
    }

    [TestMethod]
    public void NeverBypassable_EmptyActions_ReturnsEmpty()
    {
        Assert.AreEqual(0, SystemKeyGuard.FindNeverBypassableCombos([]).Count);
    }

    [TestMethod]
    public void NeverBypassable_Duplicates_AreCollapsed()
    {
        // Multiple win+l tokens collapse to a single entry.
        var hits = SystemKeyGuard.FindNeverBypassableCombos(KeyStringParser.Parse("win+l win+l"));
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual("win+l", hits[0]);
    }
}
