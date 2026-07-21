// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Experimental (PR #601 spike): runs Microsoft's officially supported Axe.Windows scanner engine
/// in-process and adapts its in-memory results into WinApp's existing <see cref="UiAuditResult"/>
/// model. This is a feature-gated alternative to the built-in heuristic audit engine; it does not
/// replace it.
/// </summary>
internal interface IAxeWindowsScanService
{
    /// <summary>Version of the loaded Axe.Windows engine assembly (e.g. "2.4.2.0").</summary>
    string EngineVersion { get; }

    /// <summary>
    /// Scan the resolved target window's UIA subtree with Axe.Windows and map every reported error
    /// to a <see cref="UiAuditIssue"/>. All Axe.Windows results are failures, so
    /// <see cref="UiAuditSummary.Fail"/> equals the mapped issue count.
    /// </summary>
    Task<UiAuditResult> ScanAsync(UiSessionInfo session, CancellationToken cancellationToken);
}
