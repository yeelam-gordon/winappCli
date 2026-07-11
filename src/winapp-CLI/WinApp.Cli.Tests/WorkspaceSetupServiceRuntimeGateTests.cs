// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the framework-dependent Windows App Runtime presence gate
/// (<see cref="IWorkspaceSetupService.IsWindowsAppRuntimeRegistered"/>), added for spec H1. A
/// <see cref="FakePackageRegistrationService"/> models which packages are registered so the gate can
/// be verified deterministically (no WinRT / real machine state). The gate must require BOTH a
/// framework package and its matching-arch DDLM, forward the resolved arch, and exclude the CBS
/// system component.
/// </summary>
[TestClass]
public class WorkspaceSetupServiceRuntimeGateTests : BaseCommandTests
{
    private FakePackageRegistrationService _fakePackageRegistration = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakePackageRegistration = new FakePackageRegistrationService();
        return services.AddSingleton<IPackageRegistrationService>(_fakePackageRegistration);
    }

    private const string FrameworkPrefix = "Microsoft.WindowsAppRuntime.";
    private const string DdlmPrefix = "Microsoft.WinAppRuntime.DDLM.";

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_FrameworkAndDdlmPresent_ReturnsTrue()
    {
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        var service = GetRequiredService<IWorkspaceSetupService>();

        Assert.IsTrue(service.IsWindowsAppRuntimeRegistered("x64"));
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_FrameworkPresentDdlmMissing_ReturnsFalse()
    {
        // The DDLM is what an unpackaged app's bootstrapper resolves; a Framework without the
        // matching DDLM must NOT be reported as registered (would crash at bootstrap).
        _fakePackageRegistration.IsPackageInstalledPredicate = prefix => prefix == FrameworkPrefix;
        var service = GetRequiredService<IWorkspaceSetupService>();

        Assert.IsFalse(service.IsWindowsAppRuntimeRegistered("x64"));
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_DdlmPresentFrameworkMissing_ReturnsFalse()
    {
        _fakePackageRegistration.IsPackageInstalledPredicate = prefix => prefix == DdlmPrefix;
        var service = GetRequiredService<IWorkspaceSetupService>();

        Assert.IsFalse(service.IsWindowsAppRuntimeRegistered("x64"));
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_ForwardsArchAndExcludesCbs()
    {
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        var service = GetRequiredService<IWorkspaceSetupService>();

        service.IsWindowsAppRuntimeRegistered("arm64");

        var frameworkCall = _fakePackageRegistration.IsPackageInstalledCalls
            .Single(c => c.NamePrefix == FrameworkPrefix);
        Assert.AreEqual("arm64", frameworkCall.Architecture, "the resolved arch must be forwarded to the framework check");
        Assert.AreEqual(".CBS.", frameworkCall.ExcludeNameSubstring, "the CBS system component must be excluded from the framework check");

        var ddlmCall = _fakePackageRegistration.IsPackageInstalledCalls
            .Single(c => c.NamePrefix == DdlmPrefix);
        Assert.AreEqual("arm64", ddlmCall.Architecture, "the resolved arch must be forwarded to the DDLM check");
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_NullArch_DefaultsToHostArchForBothChecks()
    {
        // Folder-mode / legacy callers pass null; the check must still run against a concrete host arch
        // rather than throwing or matching every arch.
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        var service = GetRequiredService<IWorkspaceSetupService>();

        service.IsWindowsAppRuntimeRegistered(null);

        var expectedArch = WorkspaceSetupService.GetSystemArchitecture();
        Assert.IsTrue(_fakePackageRegistration.IsPackageInstalledCalls.All(c => c.Architecture == expectedArch),
            "a null arch must resolve to the host architecture for both the framework and DDLM checks");
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_ExpectedVersionPresent_ReturnsTrueAndForwardsArch()
    {
        // Spec R2-M1: when the resolved runtime identities are supplied, each must also be registered
        // (for the arch). Here everything is present, so the gate passes.
        const string expected = "Microsoft.WindowsAppRuntime.1.8";
        _fakePackageRegistration.IsPackageInstalledPredicate = _ => true;
        var service = GetRequiredService<IWorkspaceSetupService>();

        Assert.IsTrue(service.IsWindowsAppRuntimeRegistered("arm64", new[] { expected }));

        var expectedCall = _fakePackageRegistration.IsPackageInstalledCalls.Single(c => c.NamePrefix == expected);
        Assert.AreEqual("arm64", expectedCall.Architecture, "the version-specific check must be arch-scoped too");
    }

    [TestMethod]
    public void IsWindowsAppRuntimeRegistered_DifferentVersionRegistered_ReturnsFalse()
    {
        // Spec R2-M1: the generic Framework + DDLM prefixes are present (a DIFFERENT WinAppSDK version
        // is registered for the arch — common on dev boxes), but the SPECIFIC version the app was built
        // against silently failed to install. The gate must fail instead of false-passing and booting an
        // app that crashes at bootstrap.
        const string required = "Microsoft.WindowsAppRuntime.1.8";
        _fakePackageRegistration.IsPackageInstalledPredicate = name => name != required;
        var service = GetRequiredService<IWorkspaceSetupService>();

        Assert.IsFalse(service.IsWindowsAppRuntimeRegistered("x64", new[] { required }));
    }

    [TestMethod]
    public void WinAppRuntimeCbsInfix_DiscriminatesCbsFromFramework()
    {
        // Spec R2-L1: guard the exclusion substring against a real CBS name vs a real Framework name,
        // rather than pinning the constant to a literal copy of itself.
        const string cbsName = "Microsoft.WindowsAppRuntime.CBS.1.8";
        const string frameworkName = "Microsoft.WindowsAppRuntime.1.8";

        Assert.IsTrue(cbsName.Contains(WorkspaceSetupService.WinAppRuntimeCbsInfix, StringComparison.Ordinal),
            "the CBS system component name must contain the exclusion infix");
        Assert.IsFalse(frameworkName.Contains(WorkspaceSetupService.WinAppRuntimeCbsInfix, StringComparison.Ordinal),
            "a real Framework package name must NOT contain the exclusion infix (so it isn't wrongly excluded)");
    }
}
