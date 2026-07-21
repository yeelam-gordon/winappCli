# Spike: direct in-process Axe.Windows under WinApp CLI NativeAOT (PR #601)

**Status: BLOCKED — direct in-process integration is not viable under NativeAOT.**
This is a fork-only experiment branch. It is **not** for upstream and must not be merged as-is.
This document is the tracked evidence; it lives under `experiments/`, never under `docs/`.

## Question

Can WinApp CLI leverage Microsoft's officially-documented Axe.Windows engine **directly in-process**
(`Config.Builder` → `ScannerFactory` → `IScanner`, the supported surface in
[axe-windows `docs/AutomationReference.md`](https://github.com/microsoft/axe-windows/blob/main/docs/AutomationReference.md))
while preserving the CLI's `PublishAot=true; SelfContained=true; TrimMode=full` publish?

## Answer

**No.** The integration compiles (Debug) and even **NativeAOT-links** into a 29.2 MB self-contained
`winapp.exe`, but the scan path **fail-fast crashes at runtime** under NativeAOT, and the real Release
publish is blocked by trim/AOT analysis warnings that the repo promotes to errors. The blockers are
inherent to Axe.Windows and are **not** fixable via the supported API, assembly rooting, or config.

## What was built (supported API only)

- Pinned `Axe.Windows` **2.4.2** (`Directory.Packages.props`, `WinApp.Cli.csproj`).
- `Services/AxeWindowsScanService.cs` — uses **only** `Config.Builder.ForProcessId(...)` →
  `.WithOutputFileFormat(OutputFileFormat.None)` → `.WithDPIAwareness(...)` → `ScannerFactory.CreateScanner`
  → `IScanner.ScanAsync`, then adapts in-memory `ScanOutput.WindowScanOutputs[].Errors[]` (`ScanResult`)
  into WinApp's existing `UiAuditResult`/`UiAuditIssue`. **No** unsupported `Rules.RunAll`/`RunRuleByID`.
  No output files. Engine version captured from the loaded `Axe.Windows.Automation` assembly.
- Feature-gated behind a **hidden** `--experimental-engine axe` flag on `ui audit` (default `heuristic`).
  The #601 heuristic engine (incl. contrast) is left fully intact and remains the default.
- `TrimRoots.xml` roots the Axe engine assemblies (targeted fix for reflection rule discovery — see below).

## Build & test results

| Gate | Result |
|------|--------|
| `dotnet build -c Debug` | **0 warnings / 0 errors** |
| Targeted audit tests (`--filter FullyQualifiedName~Audit`) | **83 passed / 0 failed** (heuristic path unaffected) |
| `dotnet publish -c Release -r win-x64 --self-contained` (repo's real AOT command) | **FAILS** — see below |

## NativeAOT results (the decisive test)

### 1. Real repo command — fails at analysis (warnings-as-errors)
`-c Release` sets `TreatWarningsAsErrors=true` (Directory.Build.props, Release only). ILC reaches the
analysis stage and promotes trim/AOT warnings to errors, failing the publish:

```
error IL2104: Assembly 'Axe.Windows.Desktop'  produced trim warnings
error IL2104: Assembly 'Axe.Windows.Actions'  produced trim warnings
error IL2104: Assembly 'Axe.Windows.Core'     produced trim warnings
error IL2104: Assembly 'Axe.Windows.Rules'    produced trim warnings
error IL2104: Assembly 'Newtonsoft.Json'      produced trim warnings
error IL3053: Assembly 'Axe.Windows.Actions'  produced AOT analysis warnings
error IL3053: Assembly 'Axe.Windows.Desktop'  produced AOT analysis warnings
error IL3053: Assembly 'Axe.Windows.Core'     produced AOT analysis warnings
error IL3053: Assembly 'Newtonsoft.Json'      produced AOT analysis warnings
error IL3053: Assembly 'Microsoft.CSharp'     produced AOT analysis warnings
error IL3053: Assembly 'System.Linq.Expressions' produced AOT analysis warnings
error MSB3077: ilc ... exited with return value 0, but errors were detected
```

### 2. Diagnostic publish — codegen + link SUCCEED, runtime CRASHES
Re-run with warnings-as-errors disabled **only to characterize the blocker** (not a shippable config):
`-p:TreatWarningsAsErrors=false -p:ILLinkTreatWarningsAsErrors=false -p:IlcTreatWarningsAsErrors=false`.

- ILC **codegen succeeds** (produced `winapp.obj`, ~188 MB) — there is **no** ILC-time
  type-equivalence / embedded-interop error.
- Native **link succeeds** (after putting `vswhere.exe` on PATH; the .NET AOT link target shells out to
  vswhere to locate `link.exe`) → **`winapp.exe`, 29.2 MB**, self-contained.
- The AOT binary runs the **heuristic** path fine (`winapp --version`, `ui audit --help` work).
- Running the **axe** path against a live app (`winapp ui audit -a <pid> --experimental-engine axe`)
  **fail-fast crashes** (exit `0xC0000409`):

```
Unhandled exception. System.NotSupportedException: PlatformNotSupported_ComInterop
   at System.Runtime.InteropServices.Marshal.GetTypeFromCLSID(Guid, String, Boolean)
   at Axe.Windows.Desktop.UIAutomation.EventHandlers.EventListenerFactory.ProcessMessageQueue()
   at System.Threading.Thread.StartThread(IntPtr)
```

The exception is thrown on an **internal Axe UIA event-listener thread**, so it is uncatchable by the
caller and aborts the whole process.

## Root cause

Two independent, fundamental blockers — both inherent to `Axe.Windows.Desktop`/`.Core`:

1. **COM activation via `Marshal.GetTypeFromCLSID` is `PlatformNotSupported` under NativeAOT.**
   Axe activates UIA COM objects (`CUIAutomation`) through CLSID activation on a background thread
   (`EventListenerFactory.ProcessMessageQueue`). NativeAOT disables built-in/`GetTypeFromCLSID` COM
   interop, so the scan aborts immediately. Not fixable via the supported API or rooting.

2. **Pervasive `dynamic` (`RequiresDynamicCode`) in the element model.** `A11yProperty`, `A11yElement`,
   `A11yPatternFactory`, `DesktopElement*` use the C# `dynamic` keyword
   (`Microsoft.CSharp.RuntimeBinder`, `CallSite.Create`), which requires DLR runtime code generation —
   incompatible with AOT. Warning profile from the diagnostic publish:

   | IL code | Meaning | Count |
   |---------|---------|------:|
   | IL3050 | RequiresDynamicCode (DLR / `dynamic`, array-of-runtime-type) | **348** |
   | IL2026 | RequiresUnreferencedCode (reflection/trim) | **166** |
   | IL2070/IL2075/IL2072/IL2080/... | reflection dataflow (`GetType()`, members) | ~90 |

   Top IL3050 sources: `A11yProperty.cs` (88), `A11yPatternFactory.cs` (74), `DesktopElementExtensionMethods.cs` (36),
   `A11yElement.cs` (26), `DesktopElement.cs` (22).

Secondary (mitigated / not the gating issue):
- **Reflection rule discovery** — `RuleFactory` enumerates rule types via
  `Assembly.GetExecutingAssembly().GetTypes()` + `Activator.CreateInstance`. Mitigated with a **targeted**
  `TrimRoots.xml` root set (not a global trim opt-out) so rules survive `TrimMode=full`.
- **Newtonsoft.Json 13.0.3** (an Axe dependency) is reflection/dynamic-heavy → its own IL2104/IL3053.

## Why it can't be fixed within the constraints

- The crash (#1) and `dynamic` (#2) live **inside** Axe assemblies; they cannot be resolved by the
  consumer via the supported API, assembly rooting, or `[RequiresDynamicCode]`/`[RequiresUnreferencedCode]`
  annotations (annotations only relocate caller warnings; the IL3050/IL2104 originate in Axe's own code).
- The only ways to "pass" would be to **disable trimming/AOT warnings broadly** (concealment — explicitly
  out of scope) and/or ship a binary that **crashes at runtime** — neither is acceptable.
- Axe ships **netstandard2.0 only** and is authored for a JIT runtime; it is not AOT/trim-annotated.

## Package / runtime footprint (if it were viable)

Axe.Windows 2.4.2 (netstandard2.0) pulls: **Newtonsoft.Json 13.0.3**, Microsoft.Win32.Registry 5.0.0,
System.Drawing.Common, System.IO.Packaging, plus satellite resource assemblies — a materially larger
dependency surface than the current dependency-light heuristic engine.

## Recommendation

**Do not pursue direct in-process Axe.Windows for the AOT CLI.** Keep the #601 heuristic engine (incl.
contrast) as the shipped path. If Axe-grade coverage is required later, the only realistic options are:
(a) a **non-AOT / JIT** side-component or separate process that hosts Axe (e.g. delegating to
`AxeWindowsCLI`) and feeds results back into WinApp's JSON model — noted here **only as fallback
analysis**, not a chosen path, since the goal was direct engine leverage; or (b) wait for an
AOT/trim-compatible Axe engine that avoids `dynamic` and `GetTypeFromCLSID` activation.

**Confidence: HIGH** — the NativeAOT binary was built and the crash + warning profile were reproduced
empirically on this machine, not inferred.

## Reproduce

```powershell
# from the worktree, inside a VS 2022 x64 dev environment (vcvars64.bat), with vswhere.exe on PATH
dotnet build src/winapp-CLI/WinApp.Cli/WinApp.Cli.csproj -c Debug            # clean
dotnet publish src/winapp-CLI/WinApp.Cli/WinApp.Cli.csproj -c Release -r win-x64 --self-contained -o bin/win-x64   # FAILS (IL2104/IL3053 as errors)

# characterize only (NOT shippable): disable warnings-as-errors to reach a linked binary, then run the axe path
dotnet publish src/winapp-CLI/WinApp.Cli/WinApp.Cli.csproj -c Release -r win-x64 --self-contained -o bin/win-x64-diag `
  -p:TreatWarningsAsErrors=false -p:ILLinkTreatWarningsAsErrors=false -p:IlcTreatWarningsAsErrors=false
.\bin\win-x64-diag\winapp.exe ui audit -a <pid-of-any-app> --experimental-engine axe   # NotSupportedException: PlatformNotSupported_ComInterop
```
