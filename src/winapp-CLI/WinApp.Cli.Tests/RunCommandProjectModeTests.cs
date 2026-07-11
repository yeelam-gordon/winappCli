// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Project-mode routing tests for <see cref="RunCommand"/>. A <see cref="FakeProjectRunService"/>
/// supplies canned build outcomes so the packaged/unpackaged launch branches can be verified without
/// invoking the real .NET SDK. See spec <c>specs/winapp-run-csproj.md</c> §7–§9.
/// </summary>
[TestClass]
public class RunCommandProjectModeTests : BaseCommandTests
{
    private FakeMsixService _fakeMsixService = null!;
    private FakeAppLauncherService _fakeAppLauncherService = null!;
    private FakeDebugOutputService _fakeDebugOutputService = null!;
    private FakeProjectRunService _fakeProjectRunService = null!;

    private const string TestManifestContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap rescap">
          <Identity Name="TestPackage" Publisher="CN=TestPublisher" Version="1.0.0.0" />
          <Properties>
            <DisplayName>Test Package</DisplayName>
            <PublisherDisplayName>Test Publisher</PublisherDisplayName>
            <Description>Test package</Description>
            <Logo>Assets\Logo.png</Logo>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Applications>
            <Application Id="TestApp" Executable="TestApp.exe" EntryPoint="TestApp.App">
              <uap:VisualElements DisplayName="Test App" Description="Test application"
                                  BackgroundColor="#777777" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\Logo.png" />
            </Application>
          </Applications>
          <Capabilities>
            <rescap:Capability Name="runFullTrust" />
          </Capabilities>
        </Package>
        """;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeMsixService = new FakeMsixService();
        _fakeAppLauncherService = new FakeAppLauncherService();
        _fakeDebugOutputService = new FakeDebugOutputService();
        _fakeProjectRunService = new FakeProjectRunService();
        return services
            .AddSingleton<IMsixService>(_fakeMsixService)
            .AddSingleton<IAppLauncherService>(_fakeAppLauncherService)
            .AddSingleton<IDebugOutputService>(_fakeDebugOutputService)
            .AddSingleton<IProjectRunService>(_fakeProjectRunService)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    private FileInfo CreateCsproj(string name = "App.csproj")
    {
        var path = Path.Combine(_tempDirectory.FullName, name);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return new FileInfo(path);
    }

    private DirectoryInfo CreateTargetDir(bool withManifest)
    {
        var dir = _tempDirectory.CreateSubdirectory($"bin_{Guid.NewGuid():N}");
        if (withManifest)
        {
            File.WriteAllText(Path.Combine(dir.FullName, "appxmanifest.xml"), TestManifestContent);
        }
        return dir;
    }

    private void SetUnpackagedOutcome(FileInfo csproj, DirectoryInfo targetDir, bool selfContained, string arch = "x64")
    {
        var exe = Path.Combine(targetDir.FullName, "App.exe");
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, exe, ProjectPackaging.Unpackaged, selfContained, arch), 0);
    }

    private void SetPackagedOutcome(FileInfo csproj, DirectoryInfo targetDir, string arch = "x64")
    {
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, null, ProjectPackaging.Packaged, false, arch), 0);
    }

    #region Unpackaged

    [TestMethod]
    public async Task ProjectMode_Unpackaged_InstallsRuntimeForArchAndLaunchesExecutable()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false, arch: "x64");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count, "Unpackaged app should launch via LaunchExecutable");
        StringAssert.Contains(_fakeAppLauncherService.LaunchExecutableCalls[0].ExePath, "App.exe");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "Unpackaged app must NOT use AUMID activation");
        Assert.AreEqual(1, _fakeMsixService.EnsureRuntimeInstalledCalls.Count, "Runtime should be installed for a non-self-contained app");
        Assert.AreEqual("x64", _fakeMsixService.EnsureRuntimeInstalledCalls[0].Architecture, "Runtime install must honor the resolved arch");
        Assert.AreEqual(csproj.FullName, _fakeMsixService.EnsureRuntimeInstalledCalls[0].ProjectFile);
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_Arm64_InstallsRuntimeForArm64()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false, arch: "arm64");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--arch", "arm64", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("arm64", _fakeMsixService.EnsureRuntimeInstalledCalls[0].Architecture);
    }

    [TestMethod]
    public async Task ProjectMode_UnpackagedSelfContained_SkipsRuntimeInstall()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: true);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count);
        Assert.AreEqual(0, _fakeMsixService.EnsureRuntimeInstalledCalls.Count, "Self-contained apps carry their own runtime — no install");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_RejectsIdentityOnlyOption()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        var command = GetRequiredService<RunCommand>();

        // --clean only makes sense for a packaged (MSIX) app.
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--clean"]);

        Assert.AreEqual(1, exitCode, "Identity-only options must be rejected for unpackaged apps");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count, "App must not launch when an invalid option was supplied");
    }

    [TestMethod]
    public async Task ProjectMode_ForcedUnpackaged_ForwardsPropertyAndLaunchesExecutable()
    {
        // C4 regression (unit level): -p WindowsPackageType=None is forwarded to the build and the app
        // runs unpackaged. The end-to-end WindowsPackageType assertion lives in the Pester sample test.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [csproj.FullName, "-p", "WindowsPackageType=None", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count);
        CollectionAssert.Contains(_fakeProjectRunService.BuildOptions[0].Properties.ToArray(), "WindowsPackageType=None",
            "User -p property must be forwarded to the build options");
    }

    #endregion

    #region Packaged

    [TestMethod]
    public async Task ProjectMode_Packaged_InstallsArchRuntimeAndLaunchesViaAumid()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        SetPackagedOutcome(csproj, targetDir, arch: "x64");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Packaged app should register a loose-layout identity");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutRuntimeCalls.Count);
        Assert.AreEqual("x64", _fakeMsixService.AddLooseLayoutRuntimeCalls[0].RuntimeArch, "Loose-layout runtime install must honor the resolved arch");
        Assert.AreEqual(csproj.FullName, _fakeMsixService.AddLooseLayoutRuntimeCalls[0].ProjectFile);
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Packaged app should launch via AUMID");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count, "Packaged app must NOT launch the apphost exe directly");
    }

    [TestMethod]
    public async Task ProjectMode_Packaged_NoManifestInOutput_Errors()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetPackagedOutcome(csproj, targetDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode, "A packaged app with no AppxManifest.xml in the output is a misconfiguration");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count);
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count);
    }

    #endregion

    #region Guardrails / errors

    [TestMethod]
    public async Task ProjectMode_SdkTooOld_ErrorsBeforeBuild()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.SdkError = "SDK too old.";
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "Build must not run when the SDK is incapable");
    }

    [TestMethod]
    public async Task ProjectMode_BuildFails_PropagatesExitCode()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(null, 7);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(7, exitCode, "A build failure must propagate the dotnet exit code");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_BuildThrowsGuardrail_Errors()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.BuildThrows = new ProjectRunException("Not a runnable project.");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_InvalidArch_ErrorsBeforeBuild()
    {
        var csproj = CreateCsproj();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--arch", "sparc", "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "An unsupported --arch must fail before building");
    }

    [TestMethod]
    public async Task ResolveInputAmbiguity_Errors()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.ResolveInputThrows = new ProjectRunException("Multiple .csproj files found.");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(1, exitCode, "Ambiguous multi-csproj input must surface an error");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_MalformedProperty_Errors()
    {
        // Spec L3: a -p value that isn't Name=Value (here, no '=') is rejected before building so it
        // never becomes a malformed '-p:' MSBuild argument.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", "NoEqualsSign", "--detach"]);

        Assert.AreEqual(1, exitCode, "A malformed -p (no '=') must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "Validation must happen before building");
    }

    [TestMethod]
    public async Task ProjectMode_ValuelessProperty_Errors()
    {
        // Spec L3: a bare -p with no value is rejected by the option arity (OneOrMore) before the
        // handler runs, rather than silently producing an empty property.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p"]);

        Assert.AreNotEqual(0, exitCode, "A valueless -p must fail to parse");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count);
    }

    #endregion

    #region Folder mode (regression)

    [TestMethod]
    public async Task FolderMode_DelegatesToPipelineWithoutRuntimeHints()
    {
        // Folder mode must pass null runtimeArch/projectFile so behavior is byte-identical to before
        // project mode existed.
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"), TestManifestContent);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count);
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutRuntimeCalls.Count);
        Assert.IsNull(_fakeMsixService.AddLooseLayoutRuntimeCalls[0].RuntimeArch, "Folder mode must not pass a runtime arch");
        Assert.IsNull(_fakeMsixService.AddLooseLayoutRuntimeCalls[0].ProjectFile, "Folder mode must not pass a project file");
    }

    #endregion
}
