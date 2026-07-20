// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;
using Windows.Win32.UI.Accessibility;

namespace WinApp.Cli.Tests;

[TestClass]
public class UiTraversalStateTests
{
    [TestMethod]
    public void ElementLimit_StopsAndReportsOnce()
    {
        var state = new UiTraversalState(
            new UiInspectionOptions
            {
                MaxElements = 2,
                CaptureDiagnostics = true,
            },
            CancellationToken.None);

        Assert.IsTrue(state.TryVisit("root"));
        Assert.IsTrue(state.TryVisit("child"));
        Assert.IsFalse(state.TryVisit("child"));
        Assert.IsFalse(state.TryVisit("child"));

        var issue = state.GetIssues().Single();
        Assert.AreEqual(UiInspectionIssueCodes.ElementLimit, issue.Code);
        Assert.AreEqual("child", issue.Selector);
        StringAssert.Contains(issue.Message, "2-element audit limit");
    }

    [TestMethod]
    public void TimeLimit_StopsWithDeterministicMessage()
    {
        var state = new UiTraversalState(
            new UiInspectionOptions
            {
                MaxDuration = TimeSpan.Zero,
                CaptureDiagnostics = true,
            },
            CancellationToken.None);

        Assert.IsFalse(state.Checkpoint("root"));

        var issue = state.GetIssues().Single();
        Assert.AreEqual(UiInspectionIssueCodes.TimeLimit, issue.Code);
        Assert.AreEqual(
            "UI Automation traversal exceeded the 0-second audit time limit; the audit tree is incomplete.",
            issue.Message);
    }

    [TestMethod]
    public void Cancellation_IsObservedBeforeProviderWork()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var state = new UiTraversalState(new UiInspectionOptions(), cts.Token);

        Assert.ThrowsExactly<OperationCanceledException>(() => state.TryVisit("root"));
        Assert.AreEqual(0, state.VisitedElements);
    }

    [TestMethod]
    public void Diagnostics_AreBoundedAndSorted()
    {
        var state = new UiTraversalState(
            new UiInspectionOptions
            {
                MaxDiagnostics = 2,
                CaptureDiagnostics = true,
            },
            CancellationToken.None);

        state.RecordIssue("z", "third", "z");
        state.RecordIssue("a", "first", "a");
        state.RecordIssue("m", "second", "m");
        state.RecordIssue("n", "omitted", "n");

        var issues = state.GetIssues();
        Assert.AreEqual(3, issues.Length);
        Assert.AreEqual(UiInspectionIssueCodes.DiagnosticLimit, issues[1].Code);
        StringAssert.Contains(issues[1].Message, "2 additional");
        Assert.AreEqual("z", issues[2].Code);
    }

    [TestMethod]
    public void CustomControlType_UsesProviderFacingName()
    {
        Assert.AreEqual(
            "Custom",
            UiAutomationService.GetControlTypeName(UIA_CONTROLTYPE_ID.UIA_CustomControlTypeId));
    }

    [TestMethod]
    public void WindowHandleContext_PreservesHostedChildBoundary()
    {
        var hostedParent = new UiElement { WindowHandle = 333, NativeWindowHandle = 222 };
        var providerOnlyChild = new UiElement();
        var rootWithoutNativeHandle = new UiElement { NativeWindowHandle = 0 };

        var hostedBoundary = UiAutomationService.ApplyWindowHandleContext(hostedParent, 111);
        UiAutomationService.ApplyWindowHandleContext(providerOnlyChild, 111, hostedBoundary);
        UiAutomationService.ApplyWindowHandleContext(rootWithoutNativeHandle, 111);

        Assert.AreEqual(111, hostedParent.WindowHandle);
        Assert.AreEqual(222, hostedParent.NativeWindowHandle);
        Assert.AreEqual(111, providerOnlyChild.WindowHandle);
        Assert.AreEqual(222, providerOnlyChild.NativeWindowHandle);
        Assert.AreEqual(111, rootWithoutNativeHandle.WindowHandle);
        Assert.AreEqual(111, rootWithoutNativeHandle.NativeWindowHandle);
    }

    [TestMethod]
    public void WindowEnumeration_ChecksTraversalBudgetBeforeNativeWork()
    {
        var state = new UiTraversalState(
            new UiInspectionOptions
            {
                MaxDuration = TimeSpan.Zero,
                CaptureDiagnostics = true,
            },
            CancellationToken.None);

        var windows = UiAutomationService.EnumerateWindows((_, _) => true, state);

        Assert.AreEqual(0, windows.Count);
        Assert.AreEqual(UiInspectionIssueCodes.TimeLimit, state.GetIssues().Single().Code);
    }
}
