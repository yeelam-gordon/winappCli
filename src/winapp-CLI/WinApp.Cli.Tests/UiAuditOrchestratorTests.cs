// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;
using WinApp.Cli.Helpers.UiAudit;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

[TestClass]
public class UiAuditOrchestratorTests
{
    private static UiAuditOrchestrator DefaultOrchestrator() => new(
    [
        new NamesAreaEngine(),
        new KeyboardAreaEngine(),
        new ScreenReaderAreaEngine(),
        new ContrastAreaEngine(),
        new RolesAreaEngine(),
    ]);

    private static UiAuditContext Context(IReadOnlyList<UiElement> elements, string profile = AuditProfile.Basic,
        Func<UiElement, double?>? contrast = null, double dpiScale = 1.0) => new()
    {
        Elements = elements,
        Profile = profile,
        NormalContrast = 4.5,
        LargeContrast = 3.0,
        DpiScale = dpiScale,
        WcagLevel = "AA",
        ContrastProvider = contrast,
    };

    [TestMethod]
    public void Resolve_EmptySelection_DefaultsToAllImplemented()
    {
        var resolved = AuditArea.Resolve([], out var invalid);
        Assert.IsNull(invalid);
        CollectionAssert.AreEqual(AuditArea.Implemented.ToArray(), resolved!.ToArray());
    }

    [TestMethod]
    public void Resolve_AllToken_ExpandsToImplemented()
    {
        var resolved = AuditArea.Resolve(["all"], out _);
        CollectionAssert.AreEqual(AuditArea.Implemented.ToArray(), resolved!.ToArray());
    }

    [TestMethod]
    public void Resolve_RepeatedAndDeduped_PreservesCanonicalOrder()
    {
        // Passed out of order + duplicated; expect canonical Implemented ordering, de-duped.
        var resolved = AuditArea.Resolve(["roles", "names", "names"], out var invalid);
        Assert.IsNull(invalid);
        CollectionAssert.AreEqual(new[] { AuditArea.Names, AuditArea.Roles }, resolved!.ToArray());
    }

    [TestMethod]
    public void Resolve_InvalidArea_ReturnsNullAndReportsToken()
    {
        var resolved = AuditArea.Resolve(["bogus"], out var invalid);
        Assert.IsNull(resolved);
        Assert.AreEqual("bogus", invalid);
    }

    [TestMethod]
    public void Profile_Normalize_AcceptsKnownRejectsUnknown()
    {
        Assert.AreEqual(AuditProfile.Basic, AuditProfile.Normalize(null));
        Assert.AreEqual(AuditProfile.Basic, AuditProfile.Normalize("AA"));
        Assert.AreEqual(AuditProfile.Thorough, AuditProfile.Normalize("THOROUGH"));
        Assert.AreEqual(AuditProfile.Thorough, AuditProfile.Normalize("aaa"));
        Assert.IsNull(AuditProfile.Normalize("deep"));
    }

    [TestMethod]
    public void KeyboardArea_TabOrder_OnlyRunsInThoroughProfile()
    {
        // Two focusable elements with a backward jump — a tab-order warning only when tab-order runs.
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = "First",  IsKeyboardFocusable = true, IsEnabled = true, X = 10, Y = 100 },
            new UiElement { Id = "e1", Type = "Button", Name = "Second", IsKeyboardFocusable = true, IsEnabled = true, X = 10, Y = 10 },
        };
        var orchestrator = DefaultOrchestrator();

        var basic = orchestrator.Run([AuditArea.Keyboard], Context(elements, AuditProfile.Basic));
        Assert.IsFalse(basic.Issues.Any(i => i.RuleId == UiAuditEngine.CheckTabOrder),
            "basic profile should not run the tab-order heuristic");

        var thorough = orchestrator.Run([AuditArea.Keyboard], Context(elements, AuditProfile.Thorough));
        Assert.IsTrue(thorough.Issues.Any(i => i.RuleId == UiAuditEngine.CheckTabOrder),
            "thorough profile should add the tab-order heuristic");
    }

    [TestMethod]
    public void Run_MergesFindingsAndSumsSummaryAcrossAreas()
    {
        // One element that fails names AND (unfocusable+interactive) keyboard.
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = false, Selector = "btn" },
        };
        var orchestrator = DefaultOrchestrator();

        var merged = orchestrator.Run([AuditArea.Names, AuditArea.Keyboard], Context(elements));

        // names -> fail (no name); keyboard -> warn (interactive, enabled, visible, not focusable).
        Assert.AreEqual(1, merged.Summary.Fail);
        Assert.AreEqual(1, merged.Summary.Warn);
        Assert.IsTrue(merged.Issues.Any(i => i.RuleId == UiAuditEngine.CheckNames));
        Assert.IsTrue(merged.Issues.Any(i => i.RuleId == UiAuditEngine.CheckKeyboard));
    }

    [TestMethod]
    public void Run_DefaultAllAreas_DoesNotTripleCountOneUnnamedElement()
    {
        // A single unnamed focusable element trips the missing-name defect in names, keyboard, and
        // screen-reader. Cross-area de-duplication must collapse it to ONE failure (canonically the
        // names finding) rather than inflating the fail count to 3.
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = true, Selector = "btn-x" },
        };
        var orchestrator = DefaultOrchestrator();

        var result = orchestrator.Run(AuditArea.Implemented, Context(elements));

        Assert.AreEqual(1, result.Summary.Fail, "missing name must not be triple-counted across areas");
        var missingNameFails = result.Issues.Count(i =>
            i.Severity == UiAuditEngine.SeverityFail && i.Selector == "btn-x");
        Assert.AreEqual(1, missingNameFails);
        // The surviving finding is the canonical names one (area order).
        Assert.AreEqual(UiAuditEngine.CheckNames, result.Issues.Single(i => i.Selector == "btn-x").RuleId);
    }

    [TestMethod]
    public void Run_SingleScreenReaderArea_StillEmitsMissingName()
    {
        // De-duplication must not suppress a defect when only one area runs.
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = true, Selector = "btn-x" },
        };
        var orchestrator = DefaultOrchestrator();

        var result = orchestrator.Run([AuditArea.ScreenReader], Context(elements));

        Assert.AreEqual(1, result.Summary.Fail);
        Assert.AreEqual(UiAuditEngine.CheckScreenReader, result.Issues.Single().RuleId);
    }

    [TestMethod]
    public void AnyRequiresContrastCapture_TrueOnlyForContrastArea()
    {
        var orchestrator = DefaultOrchestrator();
        Assert.IsTrue(orchestrator.AnyRequiresContrastCapture([AuditArea.Contrast]));
        Assert.IsFalse(orchestrator.AnyRequiresContrastCapture([AuditArea.Names, AuditArea.Roles]));
    }

    [TestMethod]
    public void ContrastArea_UsesProviderWhenSelected()
    {
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Text", Name = "Hello", Width = 100, Height = 16 },
        };
        var orchestrator = DefaultOrchestrator();

        var result = orchestrator.Run([AuditArea.Contrast], Context(elements, contrast: _ => 2.0));

        Assert.AreEqual(1, result.Summary.Fail);
        Assert.AreEqual(UiAuditEngine.CheckContrast, result.Issues.Single().RuleId);
        Assert.AreEqual(1, result.Summary.Contrast!.Attempted);
        Assert.AreEqual(1, result.Summary.Contrast.Measured);
        Assert.AreEqual(0, result.Summary.Contrast.Unmeasured);
    }

    [TestMethod]
    public void ContrastArea_PreservesTotalFailuresWhenDetailsAreCapped()
    {
        var elements = Enumerable.Range(0, UiAuditEngine.MaxDetailedContrastIssues + 2)
            .Select(i => new UiElement
            {
                Id = $"e{i}", Type = "Text", Name = $"Text {i}", Width = 100, Height = 16,
            })
            .ToArray();
        var orchestrator = DefaultOrchestrator();

        var result = orchestrator.Run([AuditArea.Contrast], Context(elements, contrast: _ => null));

        Assert.AreEqual(elements.Length, result.Summary.Fail);
        Assert.AreEqual(UiAuditEngine.MaxDetailedContrastIssues + 1, result.Issues.Length);
    }

    [TestMethod]
    public void ContrastArea_ForwardsDpiScaleToRuleEngine()
    {
        // At 150% DPI, a 24-physical-pixel box normalizes to 16px and must use the normal-text
        // threshold (4.5). Dropping DpiScale at the area-engine bridge incorrectly treats it as
        // large text and lets this 3.2 ratio pass the relaxed 3.0 threshold.
        var elements = new[]
        {
            new UiElement { Id = "e0", Type = "Text", Name = "Body", Width = 150, Height = 24 },
        };
        var orchestrator = DefaultOrchestrator();

        var result = orchestrator.Run(
            [AuditArea.Contrast],
            Context(elements, contrast: _ => 3.2, dpiScale: 1.5));

        Assert.AreEqual(1, result.Summary.Fail,
            "the area engine must preserve the target window's DPI scale");
    }
}
