// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class PackageRegistrationServiceTests
{
    private const int ERROR_INSTALL_PACKAGE_ALREADY_EXISTS = unchecked((int)0x80073CFB);
    private const int ERROR_ACCESS_DISABLED_BY_POLICY = unchecked((int)0x800704EC);

    [TestMethod]
    public void IsSideloadPolicyError_TrueForGroupPolicyHResult()
    {
        var ex = new InvalidOperationException("blocked") { HResult = ERROR_ACCESS_DISABLED_BY_POLICY };
        Assert.IsTrue(PackageRegistrationService.IsSideloadPolicyError(ex));
    }

    [TestMethod]
    public void IsSideloadPolicyError_FalseForOtherHResults()
    {
        Assert.IsFalse(PackageRegistrationService.IsSideloadPolicyError(
            new InvalidOperationException("conflict") { HResult = ERROR_INSTALL_PACKAGE_ALREADY_EXISTS }));
        Assert.IsFalse(PackageRegistrationService.IsSideloadPolicyError(
            new Exception("misc")));
    }

    [TestMethod]
    public void BuildRegistrationException_AppendsHResult()
    {
        var ex = PackageRegistrationService.BuildRegistrationException(
            "Failed to register package",
            "boom",
            unchecked((int)0x8007000B));

        StringAssert.Contains(ex.Message, "Failed to register package: boom");
        StringAssert.Contains(ex.Message, "(0x8007000B)");
    }

    [TestMethod]
    public void BuildRegistrationException_FallsBackWhenErrorTextEmpty()
    {
        var ex = PackageRegistrationService.BuildRegistrationException(
            "Failed to register package",
            errorText: null,
            hresult: null);

        StringAssert.Contains(ex.Message, "Unknown error");
    }

    [TestMethod]
    public void BuildRegistrationException_AddsConflictHintWithGenericPlaceholder_WhenIdentityUnknown()
    {
        var ex = PackageRegistrationService.BuildRegistrationException(
            "Failed to register package",
            "duplicate",
            ERROR_INSTALL_PACKAGE_ALREADY_EXISTS,
            packageIdentityName: null);

        StringAssert.Contains(ex.Message, "Get-AppxPackage <PackageName> | Remove-AppxPackage");
    }

    [TestMethod]
    public void BuildRegistrationException_SubstitutesIdentityIntoConflictHint()
    {
        var ex = PackageRegistrationService.BuildRegistrationException(
            "Failed to register package",
            "duplicate",
            ERROR_INSTALL_PACKAGE_ALREADY_EXISTS,
            packageIdentityName: "Contoso.MyApp");

        StringAssert.Contains(ex.Message, "Get-AppxPackage Contoso.MyApp | Remove-AppxPackage");
        Assert.IsFalse(ex.Message.Contains("<PackageName>"),
            "Hint must use the actual identity, not the literal placeholder");
    }

    [TestMethod]
    public void BuildRegistrationException_DoesNotAddConflictHint_ForUnrelatedHResult()
    {
        var ex = PackageRegistrationService.BuildRegistrationException(
            "Failed to register package",
            "transient",
            unchecked((int)0x80004005));

        Assert.IsFalse(ex.Message.Contains("Get-AppxPackage"),
            "Conflict hint must only appear for 0x80073CFB");
    }

    [TestMethod]
    public void BuildRegistrationException_PreservesInnerException()
    {
        var inner = new InvalidOperationException("root cause");
        var ex = PackageRegistrationService.BuildRegistrationException(
            "Failed",
            "x",
            null,
            inner: inner);

        Assert.AreSame(inner, ex.InnerException);
    }

    // --- MapArchitecture: the kernel that decides whether GetInstalledVersion/IsPackageInstalled
    // apply an arch filter. null => no filtering (folder-mode / L2 behavior); a concrete value =>
    // only matching-arch packages count as installed (project-mode arch-correctness, spec H1/M7).

    [TestMethod]
    [DataRow("x64", Windows.System.ProcessorArchitecture.X64)]
    [DataRow("X64", Windows.System.ProcessorArchitecture.X64)]
    [DataRow(" arm64 ", Windows.System.ProcessorArchitecture.Arm64)]
    [DataRow("x86", Windows.System.ProcessorArchitecture.X86)]
    public void MapArchitecture_KnownArch_MapsToWinRtArchitecture(string arch, Windows.System.ProcessorArchitecture expected)
    {
        Assert.AreEqual(expected, PackageRegistrationService.MapArchitecture(arch));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void MapArchitecture_NullOrEmpty_ReturnsNull_SoNoArchFilterIsApplied(string? arch)
    {
        // Folder-mode run passes architecture == null. MapArchitecture must return null so the
        // filter `wantedArch is not null && ...` is skipped and any installed package (including
        // a Neutral-arch one) still counts as installed — byte-for-byte the pre-project-mode
        // behavior required by the #1 hard constraint (spec L2).
        Assert.IsNull(PackageRegistrationService.MapArchitecture(arch));
    }

    [TestMethod]
    public void MapArchitecture_UnrecognizedArch_ReturnsNull()
    {
        Assert.IsNull(PackageRegistrationService.MapArchitecture("sparc"));
    }
}
