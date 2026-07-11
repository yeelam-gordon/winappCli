// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface IWorkspaceSetupService
{
    /// <summary>
    /// Finds the MSIX directory for Windows App SDK runtime packages
    /// </summary>
    /// <param name="usedVersions">Optional dictionary of package versions to look for specific installed packages</param>
    /// <returns>The path to the MSIX directory, or null if not found</returns>
    public DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null);
    public Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the Windows App Runtime framework MSIX packages (Framework / DDLM / Singleton / Main)
    /// from the given runtime MSIX directory.
    /// </summary>
    /// <param name="msixDir">The runtime MSIX directory (from the NuGet cache).</param>
    /// <param name="taskContext">Status/debug sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="architecture">
    /// Optional target architecture (<c>x64</c> / <c>arm64</c> / <c>x86</c>). When <c>null</c> (folder mode
    /// and legacy callers) the current process architecture is used, preserving existing behavior.
    /// Project mode passes the app's resolved <c>--arch</c> so an unpackaged app of a different arch gets
    /// the matching Framework/DDLM.
    /// </param>
    /// <returns>
    /// Install/error counts plus the versioned Framework/DDLM package identities discovered in the
    /// inventory, so the caller can gate on the SPECIFIC runtime the app needs (spec R2-M1).
    /// </returns>
    public Task<(int InstalledCount, int ErrorCount, IReadOnlyList<string> RuntimePackageNames)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken, string? architecture = null);

    /// <summary>
    /// Returns <c>true</c> when a framework-dependent Windows App Runtime (a versioned Framework package
    /// plus its matching-arch DDLM) is registered for the current user for <paramref name="architecture"/>.
    /// Mirrors the presence check an unpackaged WinUI app's bootstrapper performs so callers can gate the
    /// launch rather than starting an app that would fail to resolve its runtime.
    /// </summary>
    /// <param name="architecture">Target architecture (<c>x64</c> / <c>arm64</c> / <c>x86</c>); <c>null</c> uses the current process architecture.</param>
    /// <param name="expectedRuntimePackageNames">
    /// Optional versioned Framework/DDLM identities (from <see cref="InstallWindowsAppRuntimeAsync"/>) the
    /// app was built against. When supplied, each must be registered for the arch — closing the false-pass
    /// where a different WinAppSDK version is registered but the required version silently failed to install
    /// (spec R2-M1). When null/empty (folder mode / legacy callers) only the generic presence check runs.
    /// </param>
    public bool IsWindowsAppRuntimeRegistered(string? architecture, IReadOnlyList<string>? expectedRuntimePackageNames = null);
}
