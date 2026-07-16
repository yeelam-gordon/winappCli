// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.UI.Input.Pointer;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinApp.Cli.Helpers;

internal static partial class PointerInput
{
    private static readonly IPointerNativeApi DefaultNativeApi = new PointerNativeApi();

    public static void Touch(
        TouchGesture gesture,
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs)
        => Touch(gesture, contactPaths, holdMs, durationMs, DefaultNativeApi);

    internal static void Touch(
        TouchGesture gesture,
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs,
        IPointerNativeApi nativeApi)
    {
        Exception modernFailure;
        if (IsModernPointerInjectionAvailable(nativeApi))
        {
            try
            {
                InjectModernTouch(gesture, contactPaths, holdMs, durationMs, nativeApi);
                return;
            }
            catch (ModernPointerPathUnavailableException ex)
            {
                modernFailure = ex.InnerException ?? ex;
            }
        }
        else
        {
            modernFailure = new InvalidOperationException(
                "required exports CreateSyntheticPointerDevice, InjectSyntheticPointerInput, and DestroySyntheticPointerDevice are not all available");
        }

        if (!IsLegacyTouchInjectionAvailable(nativeApi))
        {
            throw TouchUnavailable(modernFailure);
        }

        try
        {
            EnsureTouchInitialized(nativeApi);
            RunTouchGesture(
                gesture,
                contactPaths,
                holdMs,
                durationMs,
                contacts => SendLegacyTouch(nativeApi, contacts));
        }
        catch (PointerNativeApiUnavailableException ex)
        {
            throw TouchUnavailable(modernFailure, ex);
        }
    }

    private static void InjectModernTouch(
        TouchGesture gesture,
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs,
        IPointerNativeApi nativeApi)
    {
        (int X, int Y) virtualScreenOrigin;
        try
        {
            virtualScreenOrigin = PointerNativeErrors.Invoke(
                "GetSystemMetrics(SM_XVIRTUALSCREEN/SM_YVIRTUALSCREEN)",
                nativeApi.GetVirtualScreenOrigin);
        }
        catch (PointerNativeApiUnavailableException ex)
        {
            throw new ModernPointerPathUnavailableException(ex);
        }

        ValidateModernTouchCoordinates(contactPaths, virtualScreenOrigin);

        nint device;
        try
        {
            device = PointerNativeErrors.Invoke(
                "CreateSyntheticPointerDevice",
                () => nativeApi.CreateSyntheticPointerDevice(
                    POINTER_INPUT_TYPE.PT_TOUCH,
                    MaxContacts,
                    POINTER_FEEDBACK_MODE.POINTER_FEEDBACK_NONE));
        }
        catch (PointerNativeApiUnavailableException ex)
        {
            throw new ModernPointerPathUnavailableException(ex);
        }

        if (device == 0)
        {
            throw new ModernPointerPathUnavailableException(
                PointerNativeErrors.Create("CreateSyntheticPointerDevice(PT_TOUCH)", nativeApi.GetLastError()));
        }

        bool anyFrameAccepted = false;
        Exception? injectionFailure = null;
        Exception? deviceCleanupFailure = null;
        try
        {
            RunTouchGesture(
                gesture,
                contactPaths,
                holdMs,
                durationMs,
                contacts =>
                {
                    SendSyntheticTouch(nativeApi, device, contacts, virtualScreenOrigin);
                    anyFrameAccepted = true;
                });
        }
        catch (Exception ex)
        {
            injectionFailure = ex;
        }
        finally
        {
            try
            {
                PointerNativeErrors.Invoke(
                    "DestroySyntheticPointerDevice",
                    () => nativeApi.DestroySyntheticPointerDevice(device));
            }
            catch (Exception ex)
            {
                deviceCleanupFailure = ex;
            }
        }

        if (injectionFailure is PointerNativeApiUnavailableException &&
            !anyFrameAccepted &&
            deviceCleanupFailure is null)
        {
            throw new ModernPointerPathUnavailableException(injectionFailure);
        }

        if (injectionFailure is not null)
        {
            if (deviceCleanupFailure is not null)
            {
                throw PointerInjectionException.Combine(
                    injectionFailure,
                    deviceCleanupFailure: deviceCleanupFailure);
            }

            throw injectionFailure;
        }

        if (deviceCleanupFailure is not null)
        {
            throw PointerInjectionException.Combine(
                new InvalidOperationException(
                    "Touch frames were injected, but the command cannot report success because synthetic pointer device cleanup failed."),
                deviceCleanupFailure: deviceCleanupFailure);
        }
    }

    private static void EnsureTouchInitialized(IPointerNativeApi nativeApi)
    {
        bool initialized = PointerNativeErrors.Invoke(
            "InitializeTouchInjection",
            () => nativeApi.InitializeTouchInjection(
                MaxContacts,
                TOUCH_FEEDBACK_MODE.TOUCH_FEEDBACK_NONE));
        if (!initialized)
        {
            throw PointerNativeErrors.Create("InitializeTouchInjection", nativeApi.GetLastError());
        }
    }

    private static bool IsModernPointerInjectionAvailable(IPointerNativeApi nativeApi)
    {
        try
        {
            return nativeApi.ModernPointerInjectionAvailable;
        }
        catch (Exception ex) when (PointerNativeErrors.IsApiUnavailable(ex))
        {
            return false;
        }
    }

    private static bool IsLegacyTouchInjectionAvailable(IPointerNativeApi nativeApi)
    {
        try
        {
            return nativeApi.LegacyTouchInjectionAvailable;
        }
        catch (Exception ex) when (PointerNativeErrors.IsApiUnavailable(ex))
        {
            return false;
        }
    }

    private static InvalidOperationException TouchUnavailable(
        Exception modernFailure,
        Exception? legacyFailure = null)
    {
        string legacyReason = legacyFailure?.Message ??
            "required exports InitializeTouchInjection and InjectTouchInput are not both available";
        return new InvalidOperationException(
            "Touch injection is unavailable: the modern synthetic-pointer path could not be used " +
            $"({modernFailure.Message}), and the legacy touch fallback is unavailable ({legacyReason}). " +
            "No touch frame was injected.");
    }

    /// <summary>Submits one frame of touch contacts via the legacy <c>InjectTouchInput</c> API.</summary>
    private static void SendLegacyTouch(IPointerNativeApi nativeApi, POINTER_TOUCH_INFO[] contacts)
    {
        bool injected = PointerNativeErrors.Invoke(
            "InjectTouchInput",
            () => nativeApi.InjectTouchInput(contacts));
        if (!injected)
        {
            throw PointerNativeErrors.Create("InjectTouchInput", nativeApi.GetLastError());
        }
    }

    /// <summary>
    /// Submits one frame of touch contacts via the synthetic-pointer device
    /// (<c>InjectSyntheticPointerInput</c>) — the modern path shared with pen injection.
    /// </summary>
    private static void SendSyntheticTouch(
        IPointerNativeApi nativeApi,
        nint device,
        POINTER_TOUCH_INFO[] contacts,
        (int X, int Y) virtualScreenOrigin)
    {
        var infos = new POINTER_TYPE_INFO[contacts.Length];
        for (int i = 0; i < contacts.Length; i++)
        {
            infos[i] = new POINTER_TYPE_INFO { type = POINTER_INPUT_TYPE.PT_TOUCH };
            infos[i].Anonymous.touchInfo = NormalizeModernTouchContact(
                contacts[i],
                virtualScreenOrigin);
        }

        bool injected = PointerNativeErrors.Invoke(
            "InjectSyntheticPointerInput",
            () => nativeApi.InjectSyntheticPointerInput(device, infos));
        if (!injected)
        {
            throw PointerNativeErrors.Create(
                "InjectSyntheticPointerInput (touch)",
                nativeApi.GetLastError());
        }
    }

    public static void Pen(
        IReadOnlyList<PointerPoint> path,
        float pressure,
        int tiltX,
        int tiltY,
        bool eraser,
        int durationMs)
        => Pen(path, pressure, tiltX, tiltY, eraser, durationMs, DefaultNativeApi);

    internal static void Pen(
        IReadOnlyList<PointerPoint> path,
        float pressure,
        int tiltX,
        int tiltY,
        bool eraser,
        int durationMs,
        IPointerNativeApi nativeApi)
    {
        if (!IsModernPointerInjectionAvailable(nativeApi))
        {
            throw PenUnavailable();
        }

        (int X, int Y) virtualScreenOrigin;
        try
        {
            virtualScreenOrigin = PointerNativeErrors.Invoke(
                "GetSystemMetrics(SM_XVIRTUALSCREEN/SM_YVIRTUALSCREEN)",
                nativeApi.GetVirtualScreenOrigin);
        }
        catch (PointerNativeApiUnavailableException ex)
        {
            throw PenUnavailable(ex);
        }

        ValidateModernPenCoordinates(path, virtualScreenOrigin);

        nint device;
        try
        {
            device = PointerNativeErrors.Invoke(
                "CreateSyntheticPointerDevice",
                () => nativeApi.CreateSyntheticPointerDevice(
                    POINTER_INPUT_TYPE.PT_PEN,
                    1,
                    POINTER_FEEDBACK_MODE.POINTER_FEEDBACK_NONE));
        }
        catch (PointerNativeApiUnavailableException ex)
        {
            throw PenUnavailable(ex);
        }

        if (device == 0)
        {
            throw PenUnavailable(
                PointerNativeErrors.Create(
                    "CreateSyntheticPointerDevice(PT_PEN)",
                    nativeApi.GetLastError()));
        }

        bool anyFrameAccepted = false;
        Exception? injectionFailure = null;
        Exception? deviceCleanupFailure = null;
        try
        {
            uint mappedPressure = (uint)Math.Clamp((int)Math.Round(pressure * PenPressureMax), 0, (int)PenPressureMax);

            InjectPenStroke(path, mappedPressure, durationMs,
                (x, y, p, flags) =>
                {
                    SendPen(
                        nativeApi,
                        device,
                        x,
                        y,
                        p,
                        tiltX,
                        tiltY,
                        eraser,
                        flags,
                        virtualScreenOrigin);
                    anyFrameAccepted = true;
                });
        }
        catch (Exception ex)
        {
            injectionFailure = ex;
        }
        finally
        {
            try
            {
                PointerNativeErrors.Invoke(
                    "DestroySyntheticPointerDevice",
                    () => nativeApi.DestroySyntheticPointerDevice(device));
            }
            catch (Exception ex)
            {
                deviceCleanupFailure = ex;
            }
        }

        if (injectionFailure is PointerNativeApiUnavailableException &&
            !anyFrameAccepted &&
            deviceCleanupFailure is null)
        {
            throw PenUnavailable(injectionFailure);
        }

        if (injectionFailure is not null)
        {
            if (deviceCleanupFailure is not null)
            {
                throw PointerInjectionException.Combine(
                    injectionFailure,
                    deviceCleanupFailure: deviceCleanupFailure);
            }

            throw injectionFailure;
        }

        if (deviceCleanupFailure is not null)
        {
            throw PointerInjectionException.Combine(
                new InvalidOperationException(
                    "Pen frames were injected, but the command cannot report success because synthetic pointer device cleanup failed."),
                deviceCleanupFailure: deviceCleanupFailure);
        }
    }

    private static InvalidOperationException PenUnavailable(Exception? failure = null)
    {
        string reason = failure is null
            ? "required exports CreateSyntheticPointerDevice, InjectSyntheticPointerInput, and DestroySyntheticPointerDevice are not all available"
            : failure.Message.TrimEnd('.');
        return new InvalidOperationException(
            $"Pen injection is unavailable: {reason}. Pen has no legacy fallback. No pen frame was injected.");
    }

    private static void SendPen(
        IPointerNativeApi nativeApi,
        nint device,
        int x,
        int y,
        uint pressure,
        int tiltX,
        int tiltY,
        bool eraser,
        POINTER_FLAGS flags,
        (int X, int Y) virtualScreenOrigin)
    {
        var penFlags = eraser ? PEN_FLAG_ERASER : PEN_FLAG_NONE;

        var info = new POINTER_TYPE_INFO
        {
            type = POINTER_INPUT_TYPE.PT_PEN,
        };
        info.Anonymous.penInfo = new POINTER_PEN_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = POINTER_INPUT_TYPE.PT_PEN,
                pointerId = 1,
                pointerFlags = flags,
                ptPixelLocation = new System.Drawing.Point(
                    NormalizeModernCoordinate(x, virtualScreenOrigin.X, "pen x"),
                    NormalizeModernCoordinate(y, virtualScreenOrigin.Y, "pen y")),
            },
            penFlags = penFlags,
            penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y,
            pressure = pressure,
            tiltX = tiltX,
            tiltY = tiltY,
        };

        bool injected = PointerNativeErrors.Invoke(
            "InjectSyntheticPointerInput",
            () => nativeApi.InjectSyntheticPointerInput(device, [info]));
        if (!injected)
        {
            throw PointerNativeErrors.Create(
                "InjectSyntheticPointerInput (pen)",
                nativeApi.GetLastError());
        }
    }
}
