// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ProjectRunServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private ProjectRunService _service = null!;

    private const string ExecutableCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string LibraryCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Library</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string TestProjectCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <IsTestProject>true</IsTestProject>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    // No inline OutputType: a static parse treats this as non-executable, but an MSBuild evaluation
    // resolves OutputType from an import (SDK/props). Used by the M5 disambiguation tests.
    private const string NoInlineOutputTypeCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    // Inline OutputType=Exe with no inline IsTestProject: a static parse treats this as a runnable
    // executable, but an MSBuild evaluation can reveal IsTestProject=true (set by the test SDK).
    private const string InlineExeNoTestFlagCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"ProjectRunServiceTests_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _service = new ProjectRunService(new FakeDotNetService(), new TestConsole(), NullLogger<ProjectRunService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { _tempDir.Delete(true); } catch { /* ignore */ }
    }

    private FileInfo WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir.FullName, name);
        File.WriteAllText(path, content);
        return new FileInfo(path);
    }

    #region BuildDotnetArguments

    [TestMethod]
    public void BuildDotnetArguments_Default_UsesBuildTargetAndRid()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildDotnetArguments(csproj, options);

        StringAssert.StartsWith(args, "build ");
        // -t:Build is REQUIRED: without it, dotnet build --getProperty only evaluates and never builds.
        StringAssert.Contains(args, "-t:Build");
        StringAssert.Contains(args, "-c Debug");
        StringAssert.Contains(args, "-r win-x64");
        StringAssert.Contains(args, "-p:Platform=x64");
        StringAssert.Contains(args, "--getProperty:TargetDir");
        StringAssert.Contains(args, "--getProperty:RunCommand");
        StringAssert.Contains(args, "--getProperty:WindowsPackageType");
        StringAssert.Contains(args, "--getProperty:OutputType");
    }

    [TestMethod]
    public void BuildDotnetArguments_Arm64_UsesArmRidAndPlatform()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Release", "arm64", null, NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildDotnetArguments(csproj, options);

        StringAssert.Contains(args, "-c Release");
        StringAssert.Contains(args, "-r win-arm64");
        StringAssert.Contains(args, "-p:Platform=ARM64");
    }

    [TestMethod]
    public void BuildDotnetArguments_NoBuild_UsesMsbuildEvaluateOnly()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildDotnetArguments(csproj, options);

        StringAssert.StartsWith(args, "msbuild ");
        // dotnet msbuild rejects -c/-r (MSB1001); the evaluate-only path must use -p: equivalents.
        Assert.IsFalse(args.Contains("-t:Build"), "no-build path must not build");
        Assert.IsFalse(args.Contains("-c Debug"), "no-build path must not pass -c");
        StringAssert.Contains(args, "-p:Configuration=Debug");
        StringAssert.Contains(args, "-p:RuntimeIdentifier=win-x64");
        StringAssert.Contains(args, "--getProperty:TargetDir");
    }

    [TestMethod]
    public void BuildDotnetArguments_UserPlatformProperty_SuppressesDerivedPlatform()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: ["Platform=ARM64"]);

        var args = ProjectRunService.BuildDotnetArguments(csproj, options);

        StringAssert.Contains(args, "-p:Platform=ARM64");
        Assert.IsFalse(args.Contains("-p:Platform=x64"), "derived Platform must not override a user-specified one");
    }

    [TestMethod]
    public void BuildDotnetArguments_UserProperties_ForwardedToBuild()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: true, Properties: ["WindowsPackageType=None", "Foo=Bar"]);

        var args = ProjectRunService.BuildDotnetArguments(csproj, options);

        StringAssert.Contains(args, "-p:WindowsPackageType=None");
        StringAssert.Contains(args, "-p:Foo=Bar");
        StringAssert.Contains(args, "--no-restore");
    }

    [TestMethod]
    public void BuildDotnetArguments_Framework_ForwardedToBuild()
    {
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", "net10.0-windows10.0.26100.0", NoBuild: false, NoRestore: false, Properties: []);

        var args = ProjectRunService.BuildDotnetArguments(csproj, options);

        StringAssert.Contains(args, "-f net10.0-windows10.0.26100.0");
    }

    [TestMethod]
    public void BuildDotnetArguments_NoBuild_DedicatedConfigAndRidWinOverUserProperty()
    {
        // Spec M2: on the --no-build (evaluate-only) path the dedicated Configuration/RID are emitted
        // as -p: too. A conflicting user -p must NOT override them — the dedicated value must be emitted
        // LAST so MSBuild's last-wins makes the dedicated flag win, matching the build path and
        // WarnOnOverriddenFlags (dedicated flag beats a same-named -p).
        var csproj = new FileInfo(Path.Combine(_tempDir.FullName, "App.csproj"));
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false,
            Properties: ["Configuration=Release", "RuntimeIdentifier=win-arm64"]);

        var args = ProjectRunService.BuildDotnetArguments(csproj, options);

        var userConfigIdx = args.IndexOf("-p:Configuration=Release", StringComparison.Ordinal);
        var dedicatedConfigIdx = args.IndexOf("-p:Configuration=Debug", StringComparison.Ordinal);
        Assert.IsTrue(userConfigIdx >= 0, "user -p:Configuration must still be forwarded to the evaluation");
        Assert.IsTrue(dedicatedConfigIdx >= 0, "dedicated Configuration must be emitted");
        Assert.IsTrue(dedicatedConfigIdx > userConfigIdx,
            "dedicated -p:Configuration must come AFTER the user -p so last-wins makes it win");

        var userRidIdx = args.IndexOf("-p:RuntimeIdentifier=win-arm64", StringComparison.Ordinal);
        var dedicatedRidIdx = args.IndexOf("-p:RuntimeIdentifier=win-x64", StringComparison.Ordinal);
        Assert.IsTrue(userRidIdx >= 0, "user -p:RuntimeIdentifier must still be forwarded");
        Assert.IsTrue(dedicatedRidIdx >= 0, "dedicated RuntimeIdentifier must be emitted");
        Assert.IsTrue(dedicatedRidIdx > userRidIdx,
            "dedicated -p:RuntimeIdentifier must come AFTER the user -p so last-wins makes it win");
    }

    #endregion

    #region TryParseSdkVersion

    [TestMethod]
    [DataRow("8.0.100", 8, 0, 100)]
    [DataRow("10.0.301", 10, 0, 301)]
    [DataRow("8.0.100-preview.1.23456", 8, 0, 100)]
    [DataRow("9.0.203", 9, 0, 203)]
    public void TryParseSdkVersion_ValidVersions_Parsed(string input, int major, int minor, int patch)
    {
        Assert.IsTrue(ProjectRunService.TryParseSdkVersion(input, out var ma, out var mi, out var pa));
        Assert.AreEqual(major, ma);
        Assert.AreEqual(minor, mi);
        Assert.AreEqual(patch, pa);
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("8.0")]
    [DataRow("")]
    public void TryParseSdkVersion_Invalid_ReturnsFalse(string input)
    {
        Assert.IsFalse(ProjectRunService.TryParseSdkVersion(input, out _, out _, out _));
    }

    #endregion

    #region ResolveInput

    [TestMethod]
    public async Task ResolveInput_CsprojFile_ReturnsProjectMode()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);

        var resolution = await _service.ResolveInputAsync(csproj, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(csproj.FullName, resolution.Csproj!.FullName);
    }

    [TestMethod]
    public async Task ResolveInput_NonCsprojFile_Throws()
    {
        var txt = WriteFile("readme.txt", "hello");

        await Assert.ThrowsExactlyAsync<ProjectRunException>(() => _service.ResolveInputAsync(txt, CancellationToken.None));
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithNoCsproj_ReturnsFolderMode()
    {
        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Folder, resolution.Mode);
        Assert.IsNull(resolution.Csproj);
    }

    [TestMethod]
    public async Task ResolveInput_DirectoryWithSingleCsproj_ReturnsProjectMode()
    {
        WriteFile("App.csproj", ExecutableCsproj);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_SingleExecutable_PicksExecutable()
    {
        // With no canned evaluation the classifier falls back to the static parse, which reads the
        // inline OutputType of these fixtures: App=WinExe (executable), Lib=Library (not).
        WriteFile("App.csproj", ExecutableCsproj);
        WriteFile("Lib.csproj", LibraryCsproj);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleExecutableCsproj_ThrowsAmbiguity()
    {
        WriteFile("App1.csproj", ExecutableCsproj);
        WriteFile("App2.csproj", ExecutableCsproj);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => _service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_ExecutablePlusTestProject_PicksExecutable()
    {
        // A test project (IsTestProject=true) is excluded from the executable set even when its
        // OutputType is Exe, so an app + its test project disambiguates to the app (spec M5).
        WriteFile("App.csproj", ExecutableCsproj);
        WriteFile("App.Tests.csproj", TestProjectCsproj);

        var resolution = await _service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_NoExecutable_ThrowsAmbiguity()
    {
        // Multiple projects, none statically executable → we cannot pick one; guide the user to
        // name a project explicitly rather than silently building a non-runnable one (spec M5).
        WriteFile("Lib1.csproj", LibraryCsproj);
        WriteFile("Lib2.csproj", LibraryCsproj);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => _service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_EvaluationDetectsExecutableFromImport_DoesNotSilentlyPickWrongProject()
    {
        // Spec M5: App.csproj gets its OutputType from an import (nothing inline) while Tool.csproj
        // declares OutputType=Exe inline. A STATIC parse sees only Tool as executable and would
        // silently build+run Tool. MSBuild evaluation reveals BOTH are runnable → ambiguity error,
        // so the wrong project is never launched behind the user's back.
        var app = WriteFile("App.csproj", NoInlineOutputTypeCsproj);
        var tool = WriteFile("Tool.csproj", InlineExeNoTestFlagCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("WinExe"), string.Empty)
                : args.Contains(tool.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Exe"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.ResolveInputAsync(_tempDir, CancellationToken.None));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

    [TestMethod]
    public async Task ResolveInput_MultipleCsproj_EvaluationDetectsTestFromImport_PicksApp()
    {
        // Spec M5: App.Tests.csproj declares OutputType=Exe inline but no inline IsTestProject (the
        // test SDK sets it via an import). A STATIC parse would treat BOTH App and App.Tests as
        // executable → ambiguity. MSBuild evaluation reveals App.Tests is a test project, so the app
        // is correctly and unambiguously selected.
        var app = WriteFile("App.csproj", ExecutableCsproj);
        var tests = WriteFile("App.Tests.csproj", InlineExeNoTestFlagCsproj);
        var dotnet = new FakeDotNetService
        {
            RunDotnetCommandHandler = args =>
                args.Contains(tests.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("Exe", isTestProject: "true"), string.Empty)
                : args.Contains(app.FullName, StringComparison.OrdinalIgnoreCase) ? (0, EvalJson("WinExe"), string.Empty)
                : (0, string.Empty, string.Empty),
        };
        var service = NewServiceWith(dotnet, out _);

        var resolution = await service.ResolveInputAsync(_tempDir, CancellationToken.None);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    private static string EvalJson(string outputType, string isTestProject = "") =>
        $$"""{ "Properties": { "OutputType": "{{outputType}}", "IsTestProject": "{{isTestProject}}" } }""";

    #endregion

    #region BuildAndResolveAsync (--json banner suppression, spec H2)

    private static ProjectRunService NewServiceWith(FakeDotNetService dotnet, out TestConsole console)
    {
        console = new TestConsole();
        return new ProjectRunService(dotnet, console, NullLogger<ProjectRunService>.Instance);
    }

    private string PackagedPropertiesJson() =>
        // TargetDir must be non-empty and the packaging must resolve to Packaged (WindowsPackageType=MSIX)
        // so BuildAndResolveAsync succeeds without needing a real apphost .exe on disk.
        $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "MSIX", "OutputType": "WinExe", "WindowsAppSDKSelfContained": "" } }""";

    [TestMethod]
    public async Task BuildAndResolveAsync_JsonMode_DoesNotPrintBuildBannerToConsole()
    {
        // Spec H2: in --json mode stdout must be pure JSON, so the human-readable "Building…" banner
        // must not be written to the (stdout) console.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: true);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution, "the canned packaged build should resolve successfully");
        Assert.IsFalse(console.Output.Contains("Building", StringComparison.OrdinalIgnoreCase),
            "--json mode must not print the build banner to stdout");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_JsonMode_BuildFailureDiagnosticsGoToStderrNotStdout()
    {
        // Spec H2: on a failed build in --json mode, dotnet's captured diagnostics must be routed to
        // stderr so the (stdout) console stays free of non-JSON noise.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        const string diag = "error NETSDK9999: totally broken build";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (1, diag, "MSB1234: also broken") };
        var service = NewServiceWith(dotnet, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: true);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNull(outcome.Resolution, "a failed build should not resolve");
        Assert.AreEqual(1, outcome.ExitCode, "the dotnet exit code should propagate");
        Assert.IsFalse(console.Output.Contains(diag, StringComparison.OrdinalIgnoreCase),
            "--json mode must not write build diagnostics to stdout");
        Assert.IsFalse(console.Output.Contains("Building", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NonJsonMode_PrintsBuildBanner()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, out var console);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        StringAssert.Contains(console.Output, "Building",
            "non-json mode should print the human-readable build banner");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_WindowsPackageTypeNone_ResolvesUnpackaged()
    {
        // Spec §7.1: WindowsPackageType=None => unpackaged; RunCommand is the launchable apphost .exe.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var exe = WriteFile("App.exe", "stub"); // must exist for the unpackaged launch path
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "{{exe.FullName.Replace("\\", "\\\\")}}", "WindowsPackageType": "None", "OutputType": "WinExe", "WindowsAppSDKSelfContained": "false" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(ProjectPackaging.Unpackaged, outcome.Resolution!.Packaging);
        Assert.AreEqual(exe.FullName, outcome.Resolution.RunCommand);
        Assert.IsFalse(outcome.Resolution.SelfContained);
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_EmptyPackageTypeWithMsixTooling_ResolvesPackaged()
    {
        // --no-build evaluate-only path: MSIX targets don't run so WindowsPackageType is empty;
        // fall back to EnableMsixTooling=true => packaged (spec §7.1).
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "", "EnableMsixTooling": "true", "OutputType": "WinExe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual(ProjectPackaging.Packaged, outcome.Resolution!.Packaging);
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_HappyPath_CarriesResolvedArchitecture()
    {
        // The resolved architecture must flow onto the resolution so the correct-arch runtime is
        // installed for the packaged/unpackaged launch (spec §8.4 / H1).
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, PackagedPropertiesJson(), string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "arm64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var outcome = await service.BuildAndResolveAsync(csproj, options, CancellationToken.None);

        Assert.IsNotNull(outcome.Resolution);
        Assert.AreEqual("arm64", outcome.Resolution!.Architecture);
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NonExecutableOutputType_Throws()
    {
        // Guardrail: a non-runnable project (OutputType=Library) must fail fast, never launch.
        var csproj = WriteFile("Lib.csproj", LibraryCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "None", "OutputType": "Library" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.BuildAndResolveAsync(csproj, options, CancellationToken.None));
        StringAssert.Contains(ex.Message, "OutputType");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_EmptyTargetDir_Throws()
    {
        // Guardrail: an empty TargetDir means we have nowhere to register/launch from (the M4 surface
        // — a braced build preamble that broke parsing used to reach here with an empty dict).
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = """{ "Properties": { "TargetDir": "", "RunCommand": "", "WindowsPackageType": "MSIX", "OutputType": "WinExe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.BuildAndResolveAsync(csproj, options, CancellationToken.None));
        StringAssert.Contains(ex.Message, "TargetDir");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_UnpackagedMissingRunCommand_Throws()
    {
        // Guardrail: an unpackaged app with no launchable .exe (empty/absent RunCommand) must error.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "", "WindowsPackageType": "None", "OutputType": "WinExe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: false, NoRestore: false, Properties: [], Json: false);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.BuildAndResolveAsync(csproj, options, CancellationToken.None));
        StringAssert.Contains(ex.Message, "unpackaged");
    }

    [TestMethod]
    public async Task BuildAndResolveAsync_NoBuildUnpackagedMissingExe_HintsToRemoveNoBuild()
    {
        // With --no-build the guardrail should point the user at removing --no-build so the exe exists.
        var csproj = WriteFile("App.csproj", ExecutableCsproj);
        var json = $$"""{ "Properties": { "TargetDir": "{{_tempDir.FullName.Replace("\\", "\\\\")}}", "RunCommand": "{{Path.Combine(_tempDir.FullName, "missing.exe").Replace("\\", "\\\\")}}", "WindowsPackageType": "None", "OutputType": "WinExe" } }""";
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, json, string.Empty) };
        var service = NewServiceWith(dotnet, out _);
        var options = new ProjectRunOptions("Debug", "x64", null, NoBuild: true, NoRestore: false, Properties: [], Json: false);

        var ex = await Assert.ThrowsExactlyAsync<ProjectRunException>(() => service.BuildAndResolveAsync(csproj, options, CancellationToken.None));
        StringAssert.Contains(ex.Message, "--no-build");
    }

    #endregion

    #region CheckSdkAsync

    [TestMethod]
    public async Task CheckSdkAsync_DotnetNotOnPath_ReturnsNotFoundError()
    {
        // Process.Start throws when dotnet is not on PATH → surfaced as an actionable install hint.
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => throw new System.ComponentModel.Win32Exception("not found") };
        var service = NewServiceWith(dotnet, out _);

        var error = await service.CheckSdkAsync(_tempDir, CancellationToken.None);

        Assert.IsNotNull(error);
        StringAssert.Contains(error!, "not found");
    }

    [TestMethod]
    public async Task CheckSdkAsync_NonZeroExit_ReturnsError()
    {
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (1, string.Empty, "boom") };
        var service = NewServiceWith(dotnet, out _);

        var error = await service.CheckSdkAsync(_tempDir, CancellationToken.None);

        Assert.IsNotNull(error);
        StringAssert.Contains(error!, "Could not determine");
    }

    [TestMethod]
    public async Task CheckSdkAsync_TooOldVersion_ReturnsError()
    {
        // 8.0.99 < 8.0.100 (the first SDK with --getProperty) → too old.
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, "8.0.99", string.Empty) };
        var service = NewServiceWith(dotnet, out _);

        var error = await service.CheckSdkAsync(_tempDir, CancellationToken.None);

        Assert.IsNotNull(error);
        StringAssert.Contains(error!, "too old");
    }

    [TestMethod]
    public async Task CheckSdkAsync_CapableVersion_ReturnsNull()
    {
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, "8.0.100", string.Empty) };
        var service = NewServiceWith(dotnet, out _);

        Assert.IsNull(await service.CheckSdkAsync(_tempDir, CancellationToken.None));
    }

    [TestMethod]
    public async Task CheckSdkAsync_NewerVersion_ReturnsNull()
    {
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, "10.0.301", string.Empty) };
        var service = NewServiceWith(dotnet, out _);

        Assert.IsNull(await service.CheckSdkAsync(_tempDir, CancellationToken.None));
    }

    [TestMethod]
    public async Task CheckSdkAsync_UnparseableVersion_ReturnsNull()
    {
        // Present but unparseable → assume a modern SDK; the build surfaces a real error if not.
        var dotnet = new FakeDotNetService { RunDotnetCommandHandler = _ => (0, "not-a-version", string.Empty) };
        var service = NewServiceWith(dotnet, out _);

        Assert.IsNull(await service.CheckSdkAsync(_tempDir, CancellationToken.None));
    }

    #endregion
}
