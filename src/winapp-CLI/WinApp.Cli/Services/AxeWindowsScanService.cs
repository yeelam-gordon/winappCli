// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Axe.Windows.Automation;
using Axe.Windows.Automation.Data;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Experimental (PR #601 spike): drives the officially supported Axe.Windows automation surface
/// documented in microsoft/axe-windows docs/AutomationReference.md —
/// <c>Config.Builder.ForProcessId</c> → <c>ScannerFactory.CreateScanner</c> → <c>IScanner.ScanAsync</c> —
/// and adapts the returned in-memory <c>ScanOutput</c> into WinApp's <see cref="UiAuditResult"/>.
/// <para>
/// Only the supported API is used. The unsupported low-level <c>Rules.RunAll</c>/<c>RunRuleByID</c>
/// surface is intentionally avoided, and no output files are written (<see cref="OutputFileFormat.None"/>).
/// </para>
/// </summary>
internal sealed class AxeWindowsScanService : IAxeWindowsScanService
{
    private readonly ILogger<AxeWindowsScanService> _logger;

    public AxeWindowsScanService(ILogger<AxeWindowsScanService> logger) => _logger = logger;

    public string EngineVersion { get; } =
        typeof(Config).Assembly.GetName().Version?.ToString() ?? "unknown";

    public async Task<UiAuditResult> ScanAsync(UiSessionInfo session, CancellationToken cancellationToken)
    {
        // Supported configuration surface only: scope to the resolved process, produce no output
        // files, and neutralize DPI handling (WinApp already declares per-monitor DPI awareness in
        // its app manifest, so the default SetProcessDpiAware pass is unnecessary and side-effecting).
        var config = Config.Builder
            .ForProcessId(session.ProcessId)
            .WithOutputFileFormat(OutputFileFormat.None)
            .WithDPIAwareness(NoOpDpiAwareness.Instance)
            .Build();

        var scanner = ScannerFactory.CreateScanner(config);

        // Reuse WinApp's already-resolved target window: scope the scan to that HWND's subtree.
        // A zero handle falls back to the full process UIA tree.
        var scanOptions = session.WindowHandle != 0
            ? new ScanOptions(scanRootWindowHandle: (nint)session.WindowHandle)
            : new ScanOptions();

        var output = await scanner.ScanAsync(scanOptions, cancellationToken).ConfigureAwait(false);

        var issues = new List<UiAuditIssue>();
        foreach (var window in output.WindowScanOutputs)
        {
            foreach (var error in window.Errors ?? Enumerable.Empty<ScanResult>())
            {
                issues.Add(MapIssue(error));
            }
        }

        _logger.LogDebug(
            "Axe.Windows {Version} scan produced {Count} error(s) across {Windows} window(s).",
            EngineVersion, issues.Count, output.WindowScanOutputs.Count);

        return new UiAuditResult
        {
            Engine = $"axe-windows {EngineVersion}",
            Summary = new UiAuditSummary { Pass = 0, Warn = 0, Fail = issues.Count },
            Issues = issues.ToArray(),
        };
    }

    /// <summary>
    /// Map one Axe.Windows <c>ScanResult</c> (rule + element) onto WinApp's issue model, preserving
    /// the supported rule metadata (ID, description, how-to-fix, standard) and best-effort element
    /// identity (accessible name, automation id).
    /// </summary>
    private static UiAuditIssue MapIssue(ScanResult error)
    {
        var rule = error.Rule;
        var props = error.Element?.Properties;

        var name = GetProperty(props, "Name");
        var automationId = GetProperty(props, "AutomationId");
        var controlType = GetProperty(props, "LocalizedControlType");

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(rule?.Description))
        {
            parts.Add(rule!.Description);
        }
        if (!string.IsNullOrWhiteSpace(controlType))
        {
            parts.Add($"[{controlType}]");
        }
        if (!string.IsNullOrWhiteSpace(rule?.HowToFix))
        {
            parts.Add($"Fix: {rule!.HowToFix}");
        }
        if (rule is not null)
        {
            parts.Add($"(standard: {rule.Standard})");
        }

        return new UiAuditIssue
        {
            RuleId = rule is not null ? $"axe.{rule.ID}" : "axe.unknown",
            Severity = UiAuditEngine.SeverityFail,
            Selector = string.IsNullOrEmpty(automationId) ? null : automationId,
            Name = string.IsNullOrEmpty(name) ? null : name,
            Message = parts.Count > 0 ? string.Join(" ", parts) : "Axe.Windows reported an accessibility error.",
        };
    }

    private static string? GetProperty(IReadOnlyDictionary<string, string>? props, string key)
        => props is not null && props.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    /// <summary>
    /// No-op <see cref="IDPIAwareness"/>: WinApp's process is already DPI-aware via its app manifest,
    /// so the scanner must not mutate process DPI state.
    /// </summary>
    private sealed class NoOpDpiAwareness : IDPIAwareness
    {
        public static readonly NoOpDpiAwareness Instance = new();

        public object Enable() => null!;

        public void Restore(object dataFromEnable)
        {
        }
    }
}
