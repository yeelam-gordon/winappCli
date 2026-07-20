// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Helpers.UiAudit;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiAuditCommand : Command, IShortDescription
{
    public string ShortDescription => "Quick-lint the current app view for accessibility and contrast issues";

    public static Option<string[]> AreaOption { get; }
    public static Option<string> LevelOption { get; }
    public static Option<string?> OutputOption { get; }

    // Depth deep enough to walk an entire window's element tree.
    private const int AuditDepth = 40;
    internal const int AuditMaxElements = 10_000;
    internal const int AuditMaxTraversalDiagnostics = 32;
    internal static readonly TimeSpan AuditMaxTraversalDuration = TimeSpan.FromSeconds(30);

    // Bounds total contrast work across the full audit, even when a provider exposes thousands
    // of textual elements. Each candidate receives an equal deterministic share.
    private const int AuditMaxContrastSamples = 4_194_304;
    internal static readonly TimeSpan AuditMaxContrastDuration = TimeSpan.FromSeconds(10);

    static UiAuditCommand()
    {
        AreaOption = new Option<string[]>("--area")
        {
            Description = "Accessibility area(s) to audit (repeatable). Allowed: " +
                          $"{string.Join(", ", AuditArea.Selectable)}. Default: all.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
        };

        LevelOption = new Option<string>("--level")
        {
            Description = "Audit depth: basic (essential rules + WCAG AA contrast thresholds) or " +
                          "thorough (deeper rules + WCAG AAA contrast thresholds). " +
                          "Aliases: aa, aaa. Default: basic.",
            DefaultValueFactory = _ => AuditProfile.Basic,
        };

        OutputOption = new Option<string?>("--output", "-o")
        {
            Description = "Write the text or JSON audit report to a file."
        };
    }

    public UiAuditCommand()
        : base("audit", "Quick-lint the currently visible view of a running app for accessibility and contrast issues. " +
               "Walks the element tree and evaluates modular audit areas (names, keyboard, " +
               "static screen-reader readiness, contrast, roles) at a chosen level (basic/thorough). " +
               "Audits one view at a time — it does not navigate; drive the other ui commands " +
               "(invoke, send-keys) to move through other pages/tabs/states and audit each. " +
               "This heuristic lint is not accessibility certification. Exits non-zero when any " +
               "fail-severity issue is found or a requested check cannot run, so it can gate CI.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(OutputOption);
        Options.Add(AreaOption);
        Options.Add(LevelOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        UiAuditOrchestrator orchestrator,
        IAnsiConsole ansiConsole,
        ILogger<UiAuditCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selector = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            var output = parseResult.GetValue(OutputOption);
            var rawAreas = parseResult.GetValue(AreaOption) ?? [];

            // Resolve the selected areas (WHAT to audit).
            var areas = AuditArea.Resolve(rawAreas, out var invalidArea);
            if (areas is null)
            {
                var msg = $"Invalid --area '{invalidArea}'. Allowed values: {string.Join(", ", AuditArea.Selectable)}.";
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, msg);
                return 1;
            }

            // Resolve the audit level (HOW DEEP).
            var level = AuditProfile.Normalize(parseResult.GetValue(LevelOption));
            if (level is null)
            {
                var msg = $"Invalid --level '{parseResult.GetValue(LevelOption)}'. Allowed values: {string.Join(", ", AuditProfile.All)}.";
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, msg);
                return 1;
            }

            // The level drives the WCAG contrast thresholds: basic => AA (4.5 / 3.0),
            // thorough => AAA (7.0 / 4.5).
            var thorough = level == AuditProfile.Thorough;
            var normalThreshold = thorough ? 7.0 : 4.5;
            var largeThreshold = thorough ? 4.5 : 3.0;
            var wcagLevel = thorough ? "AAA" : "AA";

            // Contrast is measured only when a selected area requires a pixel capture.
            var needsContrast = orchestrator.AnyRequiresContrastCapture(areas);

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);
                var inspection = await uiAutomation.InspectAsync(
                    session,
                    selector,
                    AuditDepth,
                    new UiInspectionOptions
                    {
                        MaxElements = AuditMaxElements,
                        MaxDuration = AuditMaxTraversalDuration,
                        MaxDiagnostics = AuditMaxTraversalDiagnostics,
                        CaptureDiagnostics = true,
                        // The existing promotion performs an unbounded full-tree FindAll. Audit
                        // selectors remain valid slugs while preserving the traversal budget.
                        PromoteUniqueAutomationIds = false,
                        IncludeScopedAncestorContext = true,
                    },
                    cancellationToken);
                var elements = inspection.Elements;

                if (!string.IsNullOrEmpty(selector)
                    && elements.Length == 0
                    && inspection.Issues.Length == 0)
                {
                    UiErrors.ElementNotFound(logger, selector, json);
                    return 1;
                }

                var elementCount = elements.Count(el => el.Type != "---");
                HashSet<UiElement> contrastCandidates = needsContrast
                    ? UiAuditEngine.GetContrastCandidates(elements)
                    : new HashSet<UiElement>(ReferenceEqualityComparer.Instance);

                // Build the contrast provider by capturing the window once, then sampling each
                // eligible candidate's bounding rectangle. Capture failures are reported below.
                Func<UiElement, double?>? contrastProvider = null;
                if (contrastCandidates.Count > 0)
                {
                    var ratios = await TryComputeContrastAsync(
                        session,
                        elements,
                        contrastCandidates,
                        cancellationToken);
                    if (ratios is not null)
                    {
                        contrastProvider = el => ratios.TryGetValue(el, out var r) ? r : null;
                    }
                }

                var context = new UiAuditContext
                {
                    Elements = elements,
                    Profile = level,
                    NormalContrast = normalThreshold,
                    LargeContrast = largeThreshold,
                    DpiScale = GetDpiScale(session.WindowHandle),
                    WcagLevel = wcagLevel,
                    ContrastProvider = contrastProvider,
                };
                var result = orchestrator.Run(areas, context);

                foreach (var issue in inspection.Issues
                    .OrderBy(item => item.Code, StringComparer.Ordinal)
                    .ThenBy(item => item.Selector, StringComparer.Ordinal)
                    .ThenBy(item => item.Message, StringComparer.Ordinal))
                {
                    AddAuditFailure(result, "audit", issue.Message, issue.Selector, issue.Name);
                }

                if (elementCount == 0)
                {
                    AddAuditFailure(result, "audit",
                        "No UI Automation elements were discovered, so the current view could not be audited. " +
                        "Verify that the target window is visible and runs at the same elevation.");
                }

                var exitCode = result.Summary.Fail > 0 ? 1 : 0;

                if (json)
                {
                    var payload = JsonSerializer.Serialize(result, UiJsonContext.Default.UiAuditResult);
                    ansiConsole.Profile.Out.Writer.WriteLine(payload);
                    if (!string.IsNullOrEmpty(output))
                    {
                        await WriteReportFileAsync(output, payload, cancellationToken);
                    }
                }
                else
                {
                    var report = BuildHumanReport(result, session, level, areas);
                    ansiConsole.Markup(report.Markup);
                    if (!string.IsNullOrEmpty(output))
                    {
                        await WriteReportFileAsync(output, report.PlainText, cancellationToken);
                        ansiConsole.MarkupLine($"[grey]Report written to {Markup.Escape(Path.GetFullPath(output))}[/]");
                    }
                }

                logger.LogDebug("Audit evaluated {Count} elements: pass={Pass} warn={Warn} fail={Fail}",
                    elementCount, result.Summary.Pass, result.Summary.Warn, result.Summary.Fail);
                return exitCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }

        /// <summary>
        /// Captures the target window once and samples each text element's bounding rectangle.
        /// Returns null when capture or bounded analysis could not complete.
        /// </summary>
        private async Task<Dictionary<UiElement, double?>?> TryComputeContrastAsync(
            UiSessionInfo session,
            UiElement[] elements,
            HashSet<UiElement> contrastCandidates,
            CancellationToken ct)
        {
            try
            {
                using var boundedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                boundedCts.CancelAfter(AuditMaxContrastDuration);
                var boundedToken = boundedCts.Token;
                var (pixels, width, height, originX, originY) =
                    await uiAutomation.CaptureWindowAsync(session, boundedToken);
                var ratios = new Dictionary<UiElement, double?>(ReferenceEqualityComparer.Instance);
                var samplesPerCandidate = Math.Max(
                    1,
                    Math.Min(
                        ContrastAnalyzer.MaxSamplePixels,
                        AuditMaxContrastSamples / contrastCandidates.Count));

                foreach (var el in elements)
                {
                    boundedToken.ThrowIfCancellationRequested();
                    if (!contrastCandidates.Contains(el))
                    {
                        continue;
                    }

                    // The capture belongs to the source window. NativeWindowHandle can be a child
                    // control hosted inside that window, so it must not exclude its pixels.
                    if (el.WindowHandle is { } elHwnd && elHwnd != 0 && elHwnd != session.WindowHandle)
                    {
                        ratios[el] = null;
                        continue;
                    }

                    var rect = new ContrastAnalyzer.PixelRect(
                        (int)Math.Round(el.X - originX),
                        (int)Math.Round(el.Y - originY),
                        (int)Math.Round(el.Width),
                        (int)Math.Round(el.Height));
                    if (rect.Width <= 0 || rect.Height <= 0)
                    {
                        ratios[el] = null;
                        continue;
                    }

                    // Bounds guard: only sample elements whose rect lies within the captured
                    // buffer. Anything outside the captured origin+size is a different surface —
                    // return null rather than clamping onto unrelated pixels.
                    var right = (long)rect.X + rect.Width;
                    var bottom = (long)rect.Y + rect.Height;
                    if (rect.X < 0 || rect.Y < 0 || right > width || bottom > height)
                    {
                        ratios[el] = null;
                        continue;
                    }

                    ratios[el] = ContrastAnalyzer.ComputeContrastRatio(
                        pixels,
                        width,
                        height,
                        rect,
                        samplesPerCandidate,
                        boundedToken);
                }
                return ratios;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Contrast capture or analysis failed within the {DurationSeconds}-second budget; marking the audit incomplete",
                    (int)AuditMaxContrastDuration.TotalSeconds);
                return null;
            }
        }

        private static async Task WriteReportFileAsync(string output, string content, CancellationToken ct)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(output, content, ct);
        }

        private static double GetDpiScale(long hwnd)
        {
            if (hwnd == 0)
            {
                return 1.0;
            }

            var dpi = Windows.Win32.PInvoke.GetDpiForWindow(
                new Windows.Win32.Foundation.HWND((nint)hwnd));
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }

        private static void AddAuditFailure(
            UiAuditResult result,
            string ruleId,
            string message,
            string? selector = null,
            string? name = null)
        {
            result.Issues =
            [
                .. result.Issues,
                new UiAuditIssue
                {
                    RuleId = ruleId,
                    Severity = UiAuditEngine.SeverityFail,
                    Selector = selector,
                    Name = name,
                    Message = message,
                },
            ];
            result.Summary.Fail++;
        }

        private static (string Markup, string PlainText) BuildHumanReport(
            UiAuditResult result, UiSessionInfo session, string level,
            IReadOnlyList<string> scope)
        {
            var markup = new StringBuilder();
            var plain = new StringBuilder();

            void Line(string markupText, string plainText)
            {
                markup.AppendLine(markupText);
                plain.AppendLine(plainText);
            }

            var title = session.WindowTitle ?? session.ProcessName;
            Line($"[bold]Accessibility audit:[/] {Markup.Escape(title)} (PID {session.ProcessId})",
                 $"Accessibility audit: {title} (PID {session.ProcessId})");

            var scopeText = string.Join(", ", scope);
            Line($"[grey]Areas: {scopeText} · Level: {level}[/]",
                 $"Areas: {scopeText} · Level: {level}");

            if (result.Summary.Contrast is { } contrast)
            {
                if (contrast.Attempted == 0)
                {
                    Line("[grey]Contrast coverage: no eligible visible text candidates.[/]",
                         "Contrast coverage: no eligible visible text candidates.");
                }
                else
                {
                    var coverage = $"{contrast.Attempted} attempted, {contrast.Measured} measured, {contrast.Unmeasured} unmeasured";
                    var color = contrast.Unmeasured == 0 ? "green" : "red";
                    Line($"[{color}]Contrast coverage: {coverage}.[/]",
                         $"Contrast coverage: {coverage}.");
                }
            }

            markup.AppendLine();
            plain.AppendLine();

            if (result.Issues.Length == 0)
            {
                Line("[green]✓ No accessibility issues found.[/]", "No accessibility issues found.");
            }
            else
            {
                foreach (var issue in result.Issues)
                {
                    var isFail = issue.Severity == UiAuditEngine.SeverityFail;
                    var sevMarkup = isFail ? "[red]FAIL[/]" : "[yellow]WARN[/]";
                    var sevPlain = isFail ? "FAIL" : "WARN";
                    var sel = string.IsNullOrEmpty(issue.Selector) ? "" : $" [cyan]{Markup.Escape(issue.Selector)}[/]";
                    var selPlain = string.IsNullOrEmpty(issue.Selector) ? "" : $" {issue.Selector}";
                    Line($"{sevMarkup} [grey]({issue.RuleId})[/]{sel} {Markup.Escape(issue.Message)}",
                         $"{sevPlain} ({issue.RuleId}){selPlain} {issue.Message}");
                }
            }

            markup.AppendLine();
            plain.AppendLine();

            var s = result.Summary;
            Line($"[bold]Summary:[/] [green]{s.Pass} checks passed[/], [yellow]{s.Warn} warnings[/], [red]{s.Fail} failures[/]",
                 $"Summary: {s.Pass} checks passed, {s.Warn} warnings, {s.Fail} failures");

            return (markup.ToString(), plain.ToString());
        }
    }
}
