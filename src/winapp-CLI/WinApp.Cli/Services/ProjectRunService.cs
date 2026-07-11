// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <inheritdoc cref="IProjectRunService" />
internal sealed class ProjectRunService(
    IDotNetService dotNetService,
    IAnsiConsole ansiConsole,
    ILogger<ProjectRunService> logger) : IProjectRunService
{
    /// <summary>MSBuild properties requested from the build/evaluate step (always ≥2 → JSON output).</summary>
    private static readonly string[] RequestedProperties =
    [
        "TargetDir",
        "RunCommand",
        "WindowsPackageType",
        "WindowsAppSDKSelfContained",
        "EnableMsixTooling",
        "OutputType",
    ];

    /// <inheritdoc />
    public async Task<RunInputResolution> ResolveInputAsync(FileSystemInfo input, CancellationToken cancellationToken)
    {
        // Explicit file input: must be a .csproj (the unambiguous project-mode form).
        if (input is FileInfo file)
        {
            if (!string.Equals(file.Extension, ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectRunException(
                    $"'{file.FullName}' is not a .csproj file. Pass a .csproj, a directory containing one, or a build-output folder.");
            }

            var projectDir = file.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
            return new RunInputResolution(WinAppRunMode.Project, file, projectDir);
        }

        var dir = (DirectoryInfo)input;
        List<FileInfo> csprojs;
        try
        {
            csprojs = dir.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            csprojs = [];
        }

        // No top-level .csproj → folder mode (existing, unchanged behavior). Build-output folders
        // (bin/…) fall here. This path performs NO MSBuild evaluation, so folder mode stays identical.
        if (csprojs.Count == 0)
        {
            return new RunInputResolution(WinAppRunMode.Folder, null, dir);
        }

        if (csprojs.Count == 1)
        {
            return new RunInputResolution(WinAppRunMode.Project, csprojs[0], dir);
        }

        // Multiple .csproj files — classify each via MSBuild evaluation so an executable/test project
        // is detected even when OutputType/IsTestProject come from an import (SDK defaults,
        // Directory.Build.props, the test SDK) rather than inline XML. A static parse cannot see those
        // and could silently pick the wrong project (spec M5). Evaluation falls back to the static
        // parse per-project when the SDK/restore is unavailable, so behavior never regresses.
        var executable = new List<FileInfo>();
        foreach (var csproj in csprojs)
        {
            if (await IsExecutableNonTestProjectAsync(csproj, dir, cancellationToken))
            {
                executable.Add(csproj);
            }
        }

        if (executable.Count == 1)
        {
            return new RunInputResolution(WinAppRunMode.Project, executable[0], dir);
        }

        // Zero or several runnable candidates → we cannot safely guess; require explicit selection.
        var names = string.Join(", ", csprojs.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        throw new ProjectRunException(
            $"Multiple .csproj files found in '{dir.FullName}' ({names}). Specify which project to run, e.g. 'winapp run {csprojs[0].Name}'.");
    }

    /// <summary>
    /// Classifies a candidate project as a runnable non-test executable, preferring an MSBuild
    /// evaluation of <c>OutputType</c>/<c>IsTestProject</c> (which honors imports) and falling back
    /// to the static XML parse when evaluation is unavailable (no capable SDK, project not restored).
    /// </summary>
    private async Task<bool> IsExecutableNonTestProjectAsync(FileInfo csproj, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        // Evaluate-only (no -t:Build): fast and side-effect free. Unlike a build, we only read
        // static-ish properties, so a stale/absent output is irrelevant here.
        var arguments = WindowsCommandLine.JoinArguments(
        [
            "msbuild",
            csproj.FullName,
            "--getProperty:OutputType",
            "--getProperty:IsTestProject",
        ]) ?? string.Empty;

        try
        {
            var (exitCode, stdout, _) = await dotNetService.RunDotnetCommandAsync(workingDirectory, arguments, cancellationToken);
            if (exitCode == 0)
            {
                var props = MsBuildPropertyReader.Parse(stdout, ["OutputType", "IsTestProject"]);
                if (props.Count > 0)
                {
                    var outputType = GetProp(props, "OutputType");
                    var isTest = string.Equals(GetProp(props, "IsTestProject"), "true", StringComparison.OrdinalIgnoreCase);
                    var isExecutable = string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);
                    return isExecutable && !isTest;
                }
            }

            logger.LogDebug("{UISymbol} Could not evaluate {Project} for disambiguation; falling back to static parse.", UiSymbols.Note, csproj.Name);
        }
        catch (Exception ex)
        {
            // dotnet not on PATH / evaluation failed → fall back to the static parse below.
            logger.LogDebug("{UISymbol} Evaluation of {Project} failed ({Message}); falling back to static parse.", UiSymbols.Note, csproj.Name, ex.Message);
        }

        return ProjectDetectionService.IsExecutableNonTestProject(csproj);
    }

    /// <inheritdoc />
    public async Task<string?> CheckSdkAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        const string upgradeHint =
            "Project mode requires the .NET SDK 8.0.100 or newer (for MSBuild --getProperty). Install or update it from https://aka.ms/dotnet/download.";

        int exitCode;
        string output;
        try
        {
            (exitCode, output, _) = await dotNetService.RunDotnetCommandAsync(workingDirectory, "--version", cancellationToken);
        }
        catch (Exception)
        {
            // dotnet not on PATH → Process.Start throws.
            return $"The .NET SDK was not found. {upgradeHint}";
        }

        if (exitCode != 0)
        {
            return $"Could not determine the .NET SDK version ('dotnet --version' failed). {upgradeHint}";
        }

        var versionLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(versionLine) && TryParseSdkVersion(versionLine, out var major, out var minor, out var patch))
        {
            var capable = major > 8 || (major == 8 && (minor > 0 || (minor == 0 && patch >= 100)));
            if (!capable)
            {
                return $"The .NET SDK {versionLine} is too old for project mode. {upgradeHint}";
            }
        }

        // Present but unparseable version → assume a modern SDK; the build will surface a real error
        // if --getProperty is genuinely unsupported.
        return null;
    }

    /// <inheritdoc />
    public async Task<ProjectBuildOutcome> BuildAndResolveAsync(
        FileInfo csproj,
        ProjectRunOptions options,
        CancellationToken cancellationToken)
    {
        var workingDir = csproj.Directory ?? new DirectoryInfo(Directory.GetCurrentDirectory());
        WarnOnOverriddenFlags(options);

        var arguments = BuildDotnetArguments(csproj, options);
        logger.LogDebug("{UISymbol} dotnet {Arguments}", UiSymbols.Note, arguments);

        // In --json mode stdout must be pure JSON, so the human-readable banner and any build
        // diagnostics are suppressed / routed to stderr (spec H2). dotnet's own stdout/stderr are
        // captured by RunDotnetCommandAsync (not streamed), so they never reach our stdout directly.
        if (!options.NoBuild && !options.Json)
        {
            ansiConsole.MarkupLineInterpolated($"{UiSymbols.Wrench} Building {csproj.Name} ({options.Configuration} | {options.Architecture})...");
        }

        var (exitCode, stdout, stderr) = await dotNetService.RunDotnetCommandAsync(workingDir, arguments, cancellationToken);

        if (exitCode != 0)
        {
            // Surface dotnet's own diagnostics and propagate its exit code — do not attempt to launch.
            logger.LogError("{UISymbol} Build failed for {Project} (exit code {ExitCode}).", UiSymbols.Error, csproj.Name, exitCode);
            var combined = string.Join(Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.TrimEnd()));
            if (!string.IsNullOrWhiteSpace(combined))
            {
                // Keep stdout clean for --json consumers; route build diagnostics to stderr instead.
                if (options.Json)
                {
                    Console.Error.WriteLine(combined);
                }
                else
                {
                    ansiConsole.WriteLine(combined);
                }
            }

            return new ProjectBuildOutcome(null, exitCode);
        }

        var props = MsBuildPropertyReader.Parse(stdout, RequestedProperties);

        var outputType = GetProp(props, "OutputType");
        if (!string.IsNullOrEmpty(outputType) &&
            !string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectRunException(
                $"'{csproj.Name}' is not a runnable project (OutputType='{outputType}'). 'winapp run' requires an executable project (OutputType Exe or WinExe).");
        }

        var targetDir = GetProp(props, "TargetDir");
        var runCommand = GetProp(props, "RunCommand");
        var selfContained = string.Equals(GetProp(props, "WindowsAppSDKSelfContained"), "true", StringComparison.OrdinalIgnoreCase);
        var packaging = DeterminePackaging(props, targetDir);

        if (string.IsNullOrEmpty(targetDir))
        {
            throw new ProjectRunException(
                $"Could not resolve the build output directory (TargetDir) for '{csproj.Name}'. Ensure the project builds successfully.");
        }

        if (packaging == ProjectPackaging.Unpackaged)
        {
            if (string.IsNullOrEmpty(runCommand) || !File.Exists(runCommand))
            {
                var reason = options.NoBuild
                    ? "The runnable executable was not found. Remove --no-build so the project is built first, or build it manually."
                    : "The build did not produce a runnable executable (RunCommand).";
                throw new ProjectRunException(
                    $"'{csproj.Name}' resolves to an unpackaged app but no launchable .exe is available. {reason}");
            }
        }

        var resolution = new ProjectRunResolution(
            csproj,
            targetDir,
            string.IsNullOrEmpty(runCommand) ? null : runCommand,
            packaging,
            selfContained,
            options.Architecture);

        return new ProjectBuildOutcome(resolution, 0);
    }

    /// <summary>
    /// Determines packaged vs unpackaged from the evaluated properties (spec §7.1), never from
    /// manifest presence.
    /// </summary>
    private static ProjectPackaging DeterminePackaging(IReadOnlyDictionary<string, string> props, string targetDir)
    {
        var windowsPackageType = GetProp(props, "WindowsPackageType");

        if (string.Equals(windowsPackageType, "None", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectPackaging.Unpackaged;
        }

        if (!string.IsNullOrEmpty(windowsPackageType))
        {
            // MSIX (or any other non-empty value) → packaged.
            return ProjectPackaging.Packaged;
        }

        // Unset/empty (common on the --no-build evaluate-only path, where MSIX targets don't run):
        // fall back to EnableMsixTooling or an emitted recipe.
        if (string.Equals(GetProp(props, "EnableMsixTooling"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectPackaging.Packaged;
        }

        if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
        {
            try
            {
                if (Directory.EnumerateFiles(targetDir, "*.build.appxrecipe").Any())
                {
                    return ProjectPackaging.Packaged;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Ignore — fall through to unpackaged.
            }
        }

        return ProjectPackaging.Unpackaged;
    }

    /// <summary>
    /// Builds the argument string for <c>dotnet build -t:Build</c> (default) or
    /// <c>dotnet msbuild</c> (<c>--no-build</c> evaluate-only), forwarding the same user
    /// <c>-p</c> properties to both build and evaluation (spec §8.3/§8.5).
    /// </summary>
    internal static string BuildDotnetArguments(FileInfo csproj, ProjectRunOptions options)
    {
        var rid = RunArchHelper.ToRuntimeIdentifier(options.Architecture);
        var platform = RunArchHelper.ToPlatform(options.Architecture);
        var userSpecifiesPlatform = options.Properties.Any(p => p.StartsWith("Platform=", StringComparison.OrdinalIgnoreCase));

        var tokens = new List<string>();

        if (options.NoBuild)
        {
            // dotnet msbuild does NOT accept -c/-r (MSB1001); the -p: equivalents are emitted below.
            tokens.Add("msbuild");
            tokens.Add(csproj.FullName);
        }
        else
        {
            // Combined build + property retrieval REQUIRES an explicit -t:Build; without it,
            // dotnet build --getProperty evaluates only and does not build (verified, spec §8.2).
            tokens.Add("build");
            tokens.Add(csproj.FullName);
            tokens.Add("-t:Build");
            tokens.Add("-c");
            tokens.Add(options.Configuration);
            tokens.Add("-r");
            tokens.Add(rid);
            if (options.NoRestore)
            {
                tokens.Add("--no-restore");
            }
            if (!string.IsNullOrWhiteSpace(options.Framework))
            {
                tokens.Add("-f");
                tokens.Add(options.Framework);
            }
        }

        // User -p properties come FIRST so the dedicated equivalents below win on a conflict
        // (MSBuild is last-wins for duplicate -p:). This matches the build path, where the
        // dedicated -c/-r/-f switches always beat a -p: regardless of order, so both paths behave
        // consistently: a dedicated flag beats a same-named user -p (see WarnOnOverriddenFlags).
        foreach (var property in options.Properties)
        {
            tokens.Add($"-p:{property}");
        }

        // Dedicated build inputs as -p:, emitted LAST so they take precedence over a conflicting
        // user -p. On the --no-build (msbuild) path these carry Configuration/RID (and TFM) since the
        // switches aren't accepted there; on both paths Platform is only set when the user didn't.
        if (options.NoBuild)
        {
            tokens.Add($"-p:Configuration={options.Configuration}");
            tokens.Add($"-p:RuntimeIdentifier={rid}");
            if (!string.IsNullOrWhiteSpace(options.Framework))
            {
                tokens.Add($"-p:TargetFramework={options.Framework}");
            }
        }

        if (!userSpecifiesPlatform)
        {
            tokens.Add($"-p:Platform={platform}");
        }

        foreach (var name in RequestedProperties)
        {
            tokens.Add($"--getProperty:{name}");
        }

        return WindowsCommandLine.JoinArguments(tokens) ?? string.Empty;
    }

    private void WarnOnOverriddenFlags(ProjectRunOptions options)
    {
        // Match dotnet's behavior (dedicated flag wins over a same-named -p) but leave a debug trail.
        foreach (var property in options.Properties)
        {
            var name = property.Split('=', 2)[0].Trim();
            if (name.Equals("Configuration", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RuntimeIdentifier", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug(
                    "{UISymbol} -p:{Property} is overridden by the dedicated flag (matches dotnet precedence).",
                    UiSymbols.Note, property);
            }
            else if (name.Equals("Platform", StringComparison.OrdinalIgnoreCase))
            {
                // Opposite precedence to Configuration/RID (spec R2-L2): a user -p:Platform WINS over the
                // --arch-derived Platform (which is suppressed). The RuntimeIdentifier still follows
                // --arch, so an inconsistent pair (e.g. --arch x86 -p:Platform=ARM64) builds a mismatched
                // app — warn so the divergence isn't silent.
                logger.LogDebug(
                    "{UISymbol} -p:{Property} overrides the --arch-derived Platform; the RuntimeIdentifier still follows --arch, so ensure they are consistent.",
                    UiSymbols.Note, property);
            }
        }
    }

    private static string GetProp(IReadOnlyDictionary<string, string> props, string name)
        => props.TryGetValue(name, out var value) ? value.Trim() : string.Empty;

    /// <summary>
    /// Parses the leading <c>major.minor.patch</c> of a <c>dotnet --version</c> string
    /// (e.g. <c>8.0.100</c>, <c>10.0.301</c>, <c>8.0.100-preview.1</c>).
    /// </summary>
    internal static bool TryParseSdkVersion(string versionText, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return false;
        }

        // Strip any prerelease/build suffix.
        var core = versionText.Trim();
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            core = core[..dash];
        }

        var parts = core.Split('.');
        if (parts.Length < 3)
        {
            return false;
        }

        return int.TryParse(parts[0], out major)
            && int.TryParse(parts[1], out minor)
            && int.TryParse(parts[2], out patch);
    }
}
