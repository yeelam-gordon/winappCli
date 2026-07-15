// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.Pointer;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinApp.Cli.Helpers;

internal interface IPointerNativeApi
{
    bool ModernPointerInjectionAvailable { get; }

    bool LegacyTouchInjectionAvailable { get; }

    nint CreateSyntheticPointerDevice(
        POINTER_INPUT_TYPE pointerType,
        uint maxCount,
        POINTER_FEEDBACK_MODE mode);

    bool InjectSyntheticPointerInput(
        nint device,
        ReadOnlySpan<POINTER_TYPE_INFO> pointerInfo);

    void DestroySyntheticPointerDevice(nint device);

    bool InitializeTouchInjection(uint maxCount, TOUCH_FEEDBACK_MODE mode);

    bool InjectTouchInput(ReadOnlySpan<POINTER_TOUCH_INFO> contacts);

    int GetLastError();
}

internal sealed class PointerNativeApi : IPointerNativeApi
{
    private const uint SupportedTouchContactCount = 10;
    private static readonly string[] s_modernExports =
    [
        "CreateSyntheticPointerDevice",
        "InjectSyntheticPointerInput",
        "DestroySyntheticPointerDevice",
    ];
    private static readonly string[] s_legacyExports =
    [
        "InitializeTouchInjection",
        "InjectTouchInput",
    ];
    private static readonly Lazy<bool> s_modernAvailable =
        new(() => HasUser32Exports(s_modernExports));
    private static readonly Lazy<bool> s_legacyAvailable =
        new(() => HasUser32Exports(s_legacyExports));

    private readonly object _legacyInitializationLock = new();
    private bool _legacyInitialized;

    public bool ModernPointerInjectionAvailable => s_modernAvailable.Value;

    public bool LegacyTouchInjectionAvailable => s_legacyAvailable.Value;

    public nint CreateSyntheticPointerDevice(
        POINTER_INPUT_TYPE pointerType,
        uint maxCount,
        POINTER_FEEDBACK_MODE mode)
        => PInvoke.CreateSyntheticPointerDevice(pointerType, maxCount, mode);

    public unsafe bool InjectSyntheticPointerInput(
        nint device,
        ReadOnlySpan<POINTER_TYPE_INFO> pointerInfo)
    {
        fixed (POINTER_TYPE_INFO* pointerInfoPointer = pointerInfo)
        {
            return PInvoke.InjectSyntheticPointerInput(
                (HSYNTHETICPOINTERDEVICE)device,
                pointerInfoPointer,
                (uint)pointerInfo.Length);
        }
    }

    public void DestroySyntheticPointerDevice(nint device)
        => PInvoke.DestroySyntheticPointerDevice((HSYNTHETICPOINTERDEVICE)device);

    public bool InitializeTouchInjection(uint maxCount, TOUCH_FEEDBACK_MODE mode)
    {
        lock (_legacyInitializationLock)
        {
            if (_legacyInitialized)
            {
                return true;
            }

            // The CLI supports at most ten contacts. Initialize once at that limit so a
            // one-finger gesture cannot permanently cap a later multi-contact gesture.
            _legacyInitialized = PInvoke.InitializeTouchInjection(
                Math.Max(maxCount, SupportedTouchContactCount),
                mode);
            return _legacyInitialized;
        }
    }

    public bool InjectTouchInput(ReadOnlySpan<POINTER_TOUCH_INFO> contacts)
        => PInvoke.InjectTouchInput(contacts);

    public int GetLastError() => Marshal.GetLastPInvokeError();

    private static bool HasUser32Exports(IReadOnlyList<string> exportNames)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        nint module = 0;
        try
        {
            if (!NativeLibrary.TryLoad(
                    "user32.dll",
                    typeof(PointerNativeApi).Assembly,
                    DllImportSearchPath.System32,
                    out module))
            {
                return false;
            }

            foreach (string exportName in exportNames)
            {
                if (!NativeLibrary.TryGetExport(module, exportName, out _))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            if (module != 0)
            {
                NativeLibrary.Free(module);
            }
        }
    }
}

internal sealed class PointerNativeApiUnavailableException : InvalidOperationException
{
    public PointerNativeApiUnavailableException(
        string apiName,
        Exception innerException,
        bool priorNativeCallMayHaveBeenAttempted = false)
        : base(
            $"{apiName} is unavailable because the required user32.dll entry point could not be loaded.",
            innerException)
    {
        ApiName = apiName;
        PriorNativeCallMayHaveBeenAttempted = priorNativeCallMayHaveBeenAttempted;
    }

    public string ApiName { get; }

    public bool PriorNativeCallMayHaveBeenAttempted { get; }

    public PointerNativeApiUnavailableException WithPriorNativeCallAttempt()
        => PriorNativeCallMayHaveBeenAttempted
            ? this
            : new PointerNativeApiUnavailableException(ApiName, InnerException!, true);
}

internal sealed class PointerInjectionException : InvalidOperationException
{
    private PointerInjectionException(
        Exception primaryFailure,
        Exception? cancellationFailure,
        Exception? deviceCleanupFailure)
        : base(FormatMessage(primaryFailure, cancellationFailure, deviceCleanupFailure), primaryFailure)
    {
        PrimaryFailure = primaryFailure;
        CancellationFailure = cancellationFailure;
        DeviceCleanupFailure = deviceCleanupFailure;
    }

    public Exception PrimaryFailure { get; }

    public Exception? CancellationFailure { get; }

    public Exception? DeviceCleanupFailure { get; }

    public string? CleanupDetails
    {
        get
        {
            var details = new List<string>(2);
            if (CancellationFailure is not null)
            {
                details.Add($"canceled_up_cleanup_failed: {CancellationFailure.Message}");
            }

            if (DeviceCleanupFailure is not null)
            {
                details.Add($"device_cleanup_failed: {DeviceCleanupFailure.Message}");
            }

            return details.Count == 0 ? null : string.Join("; ", details);
        }
    }

    public static PointerInjectionException Combine(
        Exception primaryFailure,
        Exception? cancellationFailure = null,
        Exception? deviceCleanupFailure = null)
    {
        if (primaryFailure is PointerInjectionException existing)
        {
            return new PointerInjectionException(
                existing.PrimaryFailure,
                cancellationFailure ?? existing.CancellationFailure,
                deviceCleanupFailure ?? existing.DeviceCleanupFailure);
        }

        return new PointerInjectionException(
            primaryFailure,
            cancellationFailure,
            deviceCleanupFailure);
    }

    private static string FormatMessage(
        Exception primaryFailure,
        Exception? cancellationFailure,
        Exception? deviceCleanupFailure)
    {
        string message = primaryFailure.Message;
        if (cancellationFailure is not null)
        {
            message += $" Best-effort canceled-UP cleanup also failed: {cancellationFailure.Message}";
        }

        if (deviceCleanupFailure is not null)
        {
            message += $" Synthetic pointer device cleanup also failed: {deviceCleanupFailure.Message}";
        }

        return message;
    }
}

internal sealed class ModernPointerPathUnavailableException : InvalidOperationException
{
    public ModernPointerPathUnavailableException(Exception innerException)
        : base(innerException.Message, innerException)
    {
    }
}

internal static class PointerNativeErrors
{
    public static InvalidOperationException Create(string apiName, int error)
        => new(
            $"{apiName} failed (Win32 error {unchecked((uint)error)}) — pointer injection is unavailable in the current Windows version, session, or integrity context.");

    public static T Invoke<T>(string apiName, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (IsApiUnavailable(ex))
        {
            throw new PointerNativeApiUnavailableException(apiName, ex);
        }
    }

    public static void Invoke(string apiName, Action call)
    {
        try
        {
            call();
        }
        catch (Exception ex) when (IsApiUnavailable(ex))
        {
            throw new PointerNativeApiUnavailableException(apiName, ex);
        }
    }

    public static bool IsApiUnavailable(Exception ex)
        => ex is EntryPointNotFoundException
            or DllNotFoundException
            or BadImageFormatException
            or PlatformNotSupportedException ||
           ex is TypeInitializationException { InnerException: Exception innerException } &&
           IsApiUnavailable(innerException);
}
