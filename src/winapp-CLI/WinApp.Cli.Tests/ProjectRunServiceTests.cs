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
    public void ResolveInput_CsprojFile_ReturnsProjectMode()
    {
        var csproj = WriteFile("App.csproj", ExecutableCsproj);

        var resolution = _service.ResolveInput(csproj);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual(csproj.FullName, resolution.Csproj!.FullName);
    }

    [TestMethod]
    public void ResolveInput_NonCsprojFile_Throws()
    {
        var txt = WriteFile("readme.txt", "hello");

        Assert.ThrowsExactly<ProjectRunException>(() => _service.ResolveInput(txt));
    }

    [TestMethod]
    public void ResolveInput_DirectoryWithNoCsproj_ReturnsFolderMode()
    {
        var resolution = _service.ResolveInput(_tempDir);

        Assert.AreEqual(WinAppRunMode.Folder, resolution.Mode);
        Assert.IsNull(resolution.Csproj);
    }

    [TestMethod]
    public void ResolveInput_DirectoryWithSingleCsproj_ReturnsProjectMode()
    {
        WriteFile("App.csproj", ExecutableCsproj);

        var resolution = _service.ResolveInput(_tempDir);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public void ResolveInput_MultipleCsproj_SingleExecutable_PicksExecutable()
    {
        WriteFile("App.csproj", ExecutableCsproj);
        WriteFile("Lib.csproj", LibraryCsproj);

        var resolution = _service.ResolveInput(_tempDir);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public void ResolveInput_MultipleExecutableCsproj_ThrowsAmbiguity()
    {
        WriteFile("App1.csproj", ExecutableCsproj);
        WriteFile("App2.csproj", ExecutableCsproj);

        var ex = Assert.ThrowsExactly<ProjectRunException>(() => _service.ResolveInput(_tempDir));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

    [TestMethod]
    public void ResolveInput_MultipleCsproj_ExecutablePlusTestProject_PicksExecutable()
    {
        // A test project (IsTestProject=true) is excluded from the executable set even when its
        // OutputType is Exe, so an app + its test project disambiguates to the app (spec M5).
        WriteFile("App.csproj", ExecutableCsproj);
        WriteFile("App.Tests.csproj", TestProjectCsproj);

        var resolution = _service.ResolveInput(_tempDir);

        Assert.AreEqual(WinAppRunMode.Project, resolution.Mode);
        Assert.AreEqual("App.csproj", resolution.Csproj!.Name);
    }

    [TestMethod]
    public void ResolveInput_MultipleCsproj_NoExecutable_ThrowsAmbiguity()
    {
        // Multiple projects, none statically executable → we cannot pick one; guide the user to
        // name a project explicitly rather than silently building a non-runnable one (spec M5).
        WriteFile("Lib1.csproj", LibraryCsproj);
        WriteFile("Lib2.csproj", LibraryCsproj);

        var ex = Assert.ThrowsExactly<ProjectRunException>(() => _service.ResolveInput(_tempDir));
        StringAssert.Contains(ex.Message, "Multiple .csproj files");
    }

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

    #endregion
}
