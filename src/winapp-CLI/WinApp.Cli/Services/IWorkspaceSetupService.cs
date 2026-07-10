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
    public Task<(int InstalledCount, int ErrorCount)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken, string? architecture = null);
}
