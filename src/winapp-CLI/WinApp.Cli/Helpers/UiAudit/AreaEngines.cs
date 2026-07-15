// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers.UiAudit;

/// <summary>
/// Base for area engines that are implemented by delegating to the low-level, pure
/// <see cref="UiAuditEngine"/> with a profile-dependent subset of rule checks. This keeps all of
/// today's rule logic in one place while presenting it through the modular area/profile surface.
/// </summary>
internal abstract class CheckBackedAreaEngine : IUiAuditAreaEngine
{
    public abstract string Area { get; }

    public virtual bool RequiresContrastCapture => false;

    /// <summary>
    /// Low-level <see cref="UiAuditEngine"/> checks this area contributes for the given profile.
    /// Return an empty set to make this a reserved (no-op) extension point.
    /// </summary>
    protected abstract IReadOnlyList<string> ResolveChecks(string profile);

    public UiAuditResult Evaluate(UiAuditContext context)
    {
        var checks = ResolveChecks(context.Profile);
        if (checks.Count == 0)
        {
            return EmptyResult;
        }

        var options = new UiAuditEngine.Options
        {
            Checks = new HashSet<string>(checks, StringComparer.OrdinalIgnoreCase),
            Profile = context.Profile,
            NormalContrast = context.NormalContrast,
            LargeContrast = context.LargeContrast,
            WcagLevel = context.WcagLevel,
        };

        return UiAuditEngine.Run(context.Elements, options, context.ContrastProvider);
    }

    protected static UiAuditResult EmptyResult => new()
    {
        Summary = new UiAuditSummary(),
        Issues = [],
    };
}

/// <summary>Accessible-name coverage on interactive/focusable elements.</summary>
internal sealed class NamesAreaEngine : CheckBackedAreaEngine
{
    public override string Area => AuditArea.Names;

    protected override IReadOnlyList<string> ResolveChecks(string profile)
        => [UiAuditEngine.CheckNames];
}

/// <summary>
/// Keyboard reachability. <see cref="AuditProfile.Basic"/> checks focusability; the more expensive
/// tab-order coherence heuristic is added at <see cref="AuditProfile.Thorough"/>.
/// </summary>
internal sealed class KeyboardAreaEngine : CheckBackedAreaEngine
{
    public override string Area => AuditArea.Keyboard;

    protected override IReadOnlyList<string> ResolveChecks(string profile)
        => profile == AuditProfile.Thorough
            ? [UiAuditEngine.CheckKeyboard, UiAuditEngine.CheckTabOrder]
            : [UiAuditEngine.CheckKeyboard];
}

/// <summary>Control-type / role clarity on actionable elements.</summary>
internal sealed class RolesAreaEngine : CheckBackedAreaEngine
{
    public override string Area => AuditArea.Roles;

    protected override IReadOnlyList<string> ResolveChecks(string profile)
        => [UiAuditEngine.CheckRoles];
}

/// <summary>WCAG color-contrast of visible text (requires captured pixels).</summary>
internal sealed class ContrastAreaEngine : CheckBackedAreaEngine
{
    public override string Area => AuditArea.Contrast;

    public override bool RequiresContrastCapture => true;

    protected override IReadOnlyList<string> ResolveChecks(string profile)
        => [UiAuditEngine.CheckContrast];
}

/// <summary>
/// Static screen-reader readiness proxy over the current UIA tree. This does not drive or observe
/// assistive technology.
/// </summary>
internal sealed class ScreenReaderAreaEngine : CheckBackedAreaEngine
{
    public override string Area => AuditArea.ScreenReader;

    // Checks what a screen reader should be able to perceive from the current UIA tree:
    // name, role clarity, and focus reachability.
    protected override IReadOnlyList<string> ResolveChecks(string profile)
        => [UiAuditEngine.CheckScreenReader];
}
