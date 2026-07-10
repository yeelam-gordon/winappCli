// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.CommandLine;
using System.Diagnostics;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal partial class RunCommand
{
    public partial class Handler
    {
        /// <summary>
        /// Project-mode entry point (spec §7/§8): build the <c>.csproj</c>, resolve its MSBuild
        /// output properties, then launch it as packaged (loose-layout register + AUMID, reusing the
        /// shared folder pipeline) or unpackaged (launch the apphost <c>.exe</c> directly). Folder
        /// mode never reaches here.
        /// </summary>
        private async Task<int> RunProjectModeAsync(
            ParseResult parseResult,
            FileInfo csproj,
            string? appArgs,
            bool isJson,
            CancellationToken cancellationToken)
        {
            // Project-mode build inputs.
            var configuration = parseResult.GetValue(ConfigurationOption) ?? "Debug";
            var archOption = parseResult.GetValue(ArchOption);
            var runtimeOption = parseResult.GetValue(RuntimeOption);
            var framework = parseResult.GetValue(FrameworkOption);
            var noBuild = parseResult.GetValue(NoBuildOption);
            var noRestore = parseResult.GetValue(NoRestoreOption);
            var properties = parseResult.GetValue(PropertyOption) ?? [];

            // Shared launch/identity options (validity depends on packaging, checked below).
            var noLaunch = parseResult.GetValue(NoLaunchOption);
            var withAlias = parseResult.GetValue(WithAliasOption);
            var debugOutput = parseResult.GetValue(DebugOutputOption);
            var unregisterOnExit = parseResult.GetValue(UnregisterOnExitOption);
            var detach = parseResult.GetValue(DetachOption);
            var clean = parseResult.GetValue(CleanOption);
            var useSymbols = parseResult.GetValue(SymbolsOption);
            var executable = parseResult.GetValue(ExecutableOption);
            var manifest = parseResult.GetValue(ManifestOption);
            var outputAppXDirectory = parseResult.GetValue(OutputAppXDirectoryOption);

            // Resolve the target architecture: --runtime's arch beats --arch; else the process arch.
            if (!TryResolveArchitecture(archOption, runtimeOption, out var architecture, out var archError))
            {
                logger.LogError("{UISymbol} {Message}", UiSymbols.Error, archError);
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, archError);
                }
                return 1;
            }

            // A capable SDK (≥ 8.0.100) is required for MSBuild --getProperty.
            var workingDir = csproj.Directory ?? new DirectoryInfo(currentDirectoryProvider.GetCurrentDirectory());
            var sdkError = await projectRunService.CheckSdkAsync(workingDir, cancellationToken);
            if (sdkError != null)
            {
                logger.LogError("{UISymbol} {Message}", UiSymbols.Error, sdkError);
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, sdkError);
                }
                return 1;
            }

            // Build (unless --no-build) and resolve the output properties. Build output streams to
            // the console, so this runs OUTSIDE the status spinner.
            var buildOptions = new ProjectRunOptions(configuration, architecture, framework, noBuild, noRestore, properties);
            ProjectBuildOutcome outcome;
            try
            {
                outcome = await projectRunService.BuildAndResolveAsync(csproj, buildOptions, cancellationToken);
            }
            catch (ProjectRunException ex)
            {
                logger.LogError("{UISymbol} {Message}", UiSymbols.Error, ex.Message);
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, ex.Message);
                }
                return 1;
            }

            if (outcome.Resolution is null)
            {
                // Build failed — dotnet already surfaced its diagnostics. Propagate its exit code.
                var code = outcome.ExitCode == 0 ? 1 : outcome.ExitCode;
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, $"Build failed (exit code {code}).");
                }
                return code;
            }

            var resolution = outcome.Resolution;

            return resolution.Packaging == ProjectPackaging.Packaged
                ? await RunPackagedProjectAsync(
                    resolution, csproj, manifest, outputAppXDirectory, appArgs,
                    noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols, executable, isJson,
                    cancellationToken)
                : await RunUnpackagedProjectAsync(
                    resolution, csproj, appArgs,
                    noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols, executable, manifest, outputAppXDirectory, isJson,
                    cancellationToken);
        }

        /// <summary>
        /// Packaged project-mode launch (spec §7.2): point the shared folder pipeline at the build's
        /// TargetDir (which contains the MSBuild-generated <c>AppxManifest.xml</c> + recipe) and pass
        /// the resolved arch + project file so the correct-arch Windows App Runtime is installed.
        /// </summary>
        private async Task<int> RunPackagedProjectAsync(
            ProjectRunResolution resolution,
            FileInfo csproj,
            FileInfo? manifest,
            DirectoryInfo? outputAppXDirectory,
            string? appArgs,
            bool noLaunch,
            bool withAlias,
            bool debugOutput,
            bool unregisterOnExit,
            bool detach,
            bool clean,
            bool useSymbols,
            string? executable,
            bool isJson,
            CancellationToken cancellationToken)
        {
            var targetDir = new DirectoryInfo(resolution.TargetDir);

            // Guardrail: packaged (per the evaluated WindowsPackageType) but no manifest in the
            // build output is a misconfiguration — surface it clearly instead of the generic
            // "manifest not found" from the shared pipeline (which would also probe the cwd).
            if (manifest == null && !FindManifest(targetDir.FullName).Exists)
            {
                var message =
                    $"'{csproj.Name}' resolves to a packaged (MSIX) app but no AppxManifest.xml was found in the build output ({targetDir.FullName}). " +
                    "Ensure the project is a packaged WinUI app (EnableMsixTooling=true with a Package.appxmanifest), or force an unpackaged run with -p:WindowsPackageType=None.";
                logger.LogError("{UISymbol} {Message}", UiSymbols.Error, message);
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, message);
                }
                return 1;
            }

            return await ExecuteRunPipelineAsync(
                targetDir, manifest, outputAppXDirectory, appArgs,
                noLaunch, withAlias, debugOutput, unregisterOnExit, detach, clean, useSymbols, executable, isJson,
                runtimeArch: resolution.Architecture, projectFile: csproj, cancellationToken);
        }

        /// <summary>
        /// Unpackaged project-mode launch (spec §7.3): ensure the (framework-dependent) Windows App
        /// Runtime is installed for the app's arch, then launch the apphost <c>.exe</c> directly.
        /// Identity-only options are rejected since there is no MSIX package.
        /// </summary>
        private async Task<int> RunUnpackagedProjectAsync(
            ProjectRunResolution resolution,
            FileInfo csproj,
            string? appArgs,
            bool noLaunch,
            bool withAlias,
            bool debugOutput,
            bool unregisterOnExit,
            bool detach,
            bool clean,
            bool useSymbols,
            string? executable,
            FileInfo? manifest,
            DirectoryInfo? outputAppXDirectory,
            bool isJson,
            CancellationToken cancellationToken)
        {
            // Reject options that only make sense for a packaged (MSIX identity) app.
            var rejected = new List<string>();
            if (noLaunch)
            {
                rejected.Add("--no-launch");
            }
            if (withAlias)
            {
                rejected.Add("--with-alias");
            }
            if (unregisterOnExit)
            {
                rejected.Add("--unregister-on-exit");
            }
            if (clean)
            {
                rejected.Add("--clean");
            }
            if (manifest != null)
            {
                rejected.Add("--manifest");
            }
            if (outputAppXDirectory != null)
            {
                rejected.Add("--output-appx-directory");
            }

            if (rejected.Count > 0)
            {
                var message =
                    $"The option(s) {string.Join(", ", rejected)} don't apply to unpackaged apps — they're only valid for packaged (MSIX) apps. " +
                    $"'{csproj.Name}' resolves to an unpackaged WinUI app (WindowsPackageType=None). Remove them, or make the app packaged to use them.";
                logger.LogError("{UISymbol} {Message}", UiSymbols.Error, message);
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, message);
                }
                return 1;
            }

            var exePath = resolution.RunCommand!; // guaranteed non-null for unpackaged by BuildAndResolveAsync
            var workingDirectory = Path.GetDirectoryName(exePath);

            // Install the framework-dependent Windows App Runtime (Framework + DDLM) the app's
            // bootstrapper needs, for the resolved arch. Self-contained apps carry their own copy.
            if (!resolution.SelfContained)
            {
                var runtimeResult = await statusService.ExecuteWithStatusAsync(
                    "Preparing Windows App Runtime...",
                    async (taskContext, ct) =>
                    {
                        try
                        {
                            await msixService.EnsureWindowsAppRuntimeInstalledAsync(csproj, resolution.Architecture, taskContext, ct);
                            return (0, "Windows App Runtime ready");
                        }
                        catch (Exception ex)
                        {
                            return (1, $"{UiSymbols.Error} Failed to prepare the Windows App Runtime: {ex.Message}");
                        }
                    },
                    cancellationToken);

                if (runtimeResult != 0)
                {
                    if (isJson)
                    {
                        PrintJson(aumid: null, processId: null, "Failed to prepare the Windows App Runtime.");
                    }
                    return runtimeResult;
                }
            }

            uint processId;
            try
            {
                processId = appLauncherService.LaunchExecutable(exePath, appArgs, workingDirectory);
            }
            catch (Exception ex)
            {
                logger.LogError("{UISymbol} Failed to launch '{Exe}': {Message}", UiSymbols.Error, exePath, ex.Message);
                if (isJson)
                {
                    PrintJson(aumid: null, processId: null, ex.Message);
                }
                return 1;
            }

            // --detach: return immediately, surfacing the PID for automation.
            if (detach)
            {
                if (isJson)
                {
                    PrintJson(aumid: null, processId, errorMessage: null);
                }
                else
                {
                    ansiConsole.WriteLine(processId.ToString());
                }
                return 0;
            }

            if (isJson)
            {
                PrintJson(aumid: null, processId, errorMessage: null);
            }

            // --debug-output: attach the debug event loop instead of a plain wait.
            if (debugOutput)
            {
                var debugExit = await debugOutputService.RunDebugLoopAsync(processId, cancellationToken, useSymbols,
                    symbolSearchPaths: [resolution.TargetDir]);
                if (cancellationToken.IsCancellationRequested)
                {
                    appLauncherService.TerminatePackageProcesses(packageFullName: null, processId);
                }
                return debugExit;
            }

            return await WaitForLaunchedProcessAsync(processId, cancellationToken);
        }

        /// <summary>
        /// Waits for a directly-launched (unpackaged) process to exit and returns its exit code,
        /// mirroring the folder-mode wait semantics (already-exited and Ctrl+C are handled).
        /// </summary>
        private async Task<int> WaitForLaunchedProcessAsync(uint processId, CancellationToken cancellationToken)
        {
            if (processId > int.MaxValue)
            {
                return 0;
            }

            try
            {
                using var process = Process.GetProcessById(unchecked((int)processId));
                await process.WaitForExitAsync(cancellationToken);
                return process.ExitCode;
            }
            catch (ArgumentException)
            {
                // Process already exited before we could attach — treat as success.
                return 0;
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C — kill the launched process before exiting.
                appLauncherService.TerminatePackageProcesses(packageFullName: null, processId);
                return -1;
            }
        }

        /// <summary>
        /// Resolves the canonical target architecture from <c>--arch</c> / <c>--runtime</c>.
        /// <c>--runtime</c>'s architecture wins over <c>--arch</c> (mirrors dotnet, where a RID is
        /// more specific); when neither is given, the current process architecture is used.
        /// </summary>
        internal static bool TryResolveArchitecture(string? archOption, string? runtimeOption, out string architecture, out string? error)
        {
            error = null;
            architecture = string.Empty;

            string? fromRuntime = null;
            if (!string.IsNullOrWhiteSpace(runtimeOption))
            {
                fromRuntime = RunArchHelper.ArchitectureFromRid(runtimeOption);
                if (fromRuntime == null)
                {
                    error = $"Could not determine an architecture from --runtime '{runtimeOption}'. Use a RID such as win-x64, win-arm64, or win-x86.";
                    return false;
                }
            }

            string? fromArch = null;
            if (!string.IsNullOrWhiteSpace(archOption))
            {
                fromArch = RunArchHelper.NormalizeArchitecture(archOption);
                if (fromArch == null)
                {
                    error = $"Unsupported --arch '{archOption}'. Supported values: {string.Join(", ", RunArchHelper.SupportedArchitectures)}.";
                    return false;
                }
            }

            architecture = fromRuntime ?? fromArch ?? RunArchHelper.DefaultArchitecture();
            return true;
        }
    }
}
