// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed class UiInspectionOptions
{
    public int MaxElements { get; init; } = int.MaxValue;
    public TimeSpan MaxDuration { get; init; } = Timeout.InfiniteTimeSpan;
    public int MaxDiagnostics { get; init; } = 32;
    public bool CaptureDiagnostics { get; init; }
    public bool PromoteUniqueAutomationIds { get; init; } = true;
}

internal sealed class UiInspectionResult
{
    public UiElement[] Elements { get; init; } = [];
    public UiInspectionIssue[] Issues { get; init; } = [];
}

internal sealed class UiInspectionIssue
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Selector { get; init; }
    public string? Name { get; init; }
}

internal static class UiInspectionIssueCodes
{
    public const string ChildEnumeration = "child-enumeration";
    public const string DepthLimit = "depth-limit";
    public const string DiagnosticLimit = "diagnostic-limit";
    public const string ElementLimit = "element-limit";
    public const string SiblingEnumeration = "sibling-enumeration";
    public const string TimeLimit = "time-limit";
}

internal sealed class UiTraversalState
{
    private readonly UiInspectionOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly List<UiInspectionIssue> _issues = [];
    private readonly HashSet<(string Code, string Selector, string Message)> _seen = [];
    private int _omittedIssueCount;

    public UiTraversalState(UiInspectionOptions options, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxElements, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDiagnostics, 1);
        if (options.MaxDuration < TimeSpan.Zero && options.MaxDuration != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxDuration,
                "MaxDuration must be non-negative or Timeout.InfiniteTimeSpan.");
        }

        _options = options;
        _cancellationToken = cancellationToken;
    }

    public bool IsStopped { get; private set; }
    public bool CaptureDiagnostics => _options.CaptureDiagnostics;
    public int VisitedElements { get; private set; }

    public bool TryVisit(string? parentSelector)
    {
        if (!Checkpoint(parentSelector))
        {
            return false;
        }

        if (VisitedElements >= _options.MaxElements)
        {
            IsStopped = true;
            RecordIssue(
                UiInspectionIssueCodes.ElementLimit,
                $"UI Automation traversal reached the {_options.MaxElements}-element audit limit; the audit tree is incomplete.",
                parentSelector);
            return false;
        }

        VisitedElements++;
        return true;
    }

    public bool Checkpoint(string? selector = null)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (IsStopped)
        {
            return false;
        }

        if (_options.MaxDuration != Timeout.InfiniteTimeSpan
            && _stopwatch.Elapsed >= _options.MaxDuration)
        {
            IsStopped = true;
            RecordIssue(
                UiInspectionIssueCodes.TimeLimit,
                $"UI Automation traversal exceeded the {(int)_options.MaxDuration.TotalSeconds}-second audit time limit; the audit tree is incomplete.",
                selector);
            return false;
        }

        return true;
    }

    public void RecordIssue(string code, string message, string? selector = null, string? name = null)
    {
        if (!_options.CaptureDiagnostics)
        {
            return;
        }

        var key = (code, selector ?? string.Empty, message);
        if (!_seen.Add(key))
        {
            return;
        }

        if (_issues.Count < _options.MaxDiagnostics)
        {
            _issues.Add(new UiInspectionIssue
            {
                Code = code,
                Message = message,
                Selector = selector,
                Name = name,
            });
            return;
        }

        _omittedIssueCount++;
    }

    public UiInspectionIssue[] GetIssues()
    {
        IEnumerable<UiInspectionIssue> issues = _issues;
        if (_omittedIssueCount > 0)
        {
            issues = issues.Append(new UiInspectionIssue
            {
                Code = UiInspectionIssueCodes.DiagnosticLimit,
                Message = $"{_omittedIssueCount} additional UI Automation traversal failure(s) were omitted after the {_options.MaxDiagnostics}-diagnostic audit limit; the audit tree is incomplete.",
            });
        }

        return issues
            .OrderBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.Selector, StringComparer.Ordinal)
            .ThenBy(issue => issue.Message, StringComparer.Ordinal)
            .ToArray();
    }
}
