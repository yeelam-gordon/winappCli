// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

#if WINDOWS10_0_19041_0_OR_GREATER
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Includes separately rendered UI, such as menus, flyouts, tooltips, and floating tool windows,
/// in the selected window's captured image. Only visible, intersecting popup or tool windows
/// in the same process whose Win32 owner chain leads to the selected window are included.
/// Uses Windows graphics capture first and <see cref="SecondaryWindowsCaptureFallback"/> when
/// Windows rejects a secondary window. Does not create or display UI.
/// </summary>
internal sealed partial class SecondaryWindowsCapture : IFrameGrabber
{
    internal readonly record struct Popup(nint Handle, PointerRect Bounds);
    private readonly record struct PopupInput(Popup Popup, IFrameGrabber Grabber, long Version, int Width, int Height);
    private readonly IFrameGrabber _root;
    private readonly Func<PointerRect> _rootBounds;
    private readonly Func<List<Popup>> _discover;
    private readonly Func<nint, IFrameGrabber> _start;
    private readonly Func<long> _clock;
    private readonly Func<bool> _isRootValid;
    private readonly ILogger _logger;
    private readonly int? _expectedPid;
    private readonly Dictionary<nint, IFrameGrabber> _children = [];
    private readonly Dictionary<nint, long> _firstFrameDeadlines = [];
    private readonly Lock _lock = new();
    private bool _disposed;
    private bool _closed;
    private long _version;
    private long _rootVersion;
    private PointerRect _bounds;
    private PopupInput[] _popupInputs = [];
    private (byte[] Pixels, int Width, int Height, long Version)? _latest;

    internal SecondaryWindowsCapture(IFrameGrabber root, Func<PointerRect> rootBounds,
        Func<List<Popup>> discover, Func<nint, IFrameGrabber> start,
        Func<long>? clock = null, Func<bool>? isRootValid = null, ILogger? logger = null, int? expectedPid = null)
    {
        _root = root;
        _rootBounds = rootBounds;
        _discover = discover;
        _start = start;
        _clock = clock ?? (() => Environment.TickCount64);
        _isRootValid = isRootValid ?? (() => true);
        _logger = logger ?? NullLogger.Instance;
        _expectedPid = expectedPid;
    }

    internal static SecondaryWindowsCapture Start(HWND hwnd, ILogger logger, int fps,
        Func<HWND, ILogger, int, IFrameGrabber>? startWindow = null)
    {
        startWindow ??= (window, log, rate) => WgcCapture.StartSingleWindowGrabber(window, log, rate);
        var pid = RealOwnedWindowFinder.s_getWindowProcessId(hwnd);
        IFrameGrabber root;
        try
        {
            root = startWindow(hwnd, logger, fps);
        }
        catch (WgcCapture.UnsupportedCaptureWindowException ex) when (IsVisibleOwnedPopupTarget(hwnd, pid))
        {
            root = StartPrintWindowFallback(hwnd, logger, ex, pid);
        }
        return new(root,
            () => GetBounds(hwnd), () => Discover(hwnd, pid, logger),
            child => startWindow(new HWND(child), logger, fps),
            isRootValid: () => RootIsValid(hwnd, pid), logger: logger, expectedPid: pid);
    }

    private static bool IsVisibleOwnedPopupTarget(HWND hwnd, int pid)
    {
        if (pid == 0 || !RootIsValid(hwnd, pid) || !RealOwnedWindowFinder.s_isWindowVisible(hwnd))
        {
            return false;
        }
        var owner = RealOwnedWindowFinder.s_getWindowOwner(hwnd);
        if (owner.IsNull || !RootIsValid(owner, pid))
        {
            return false;
        }
        return ((uint)GetWindowLong((nint)hwnd, -16) & 0x80000000) != 0 ||
            ((uint)GetWindowLong((nint)hwnd, -20) & 0x00000080) != 0;
    }

    private static SecondaryWindowsCaptureFallback StartPrintWindowFallback(HWND hwnd, ILogger logger, Exception cause, int? expectedPid = null)
    {
        logger.LogWarning(cause, "Windows graphics capture rejected secondary window {Hwnd}; using window-only rendering (PrintWindow).", (nint)hwnd);
        return new SecondaryWindowsCaptureFallback(hwnd, logger: logger, expectedPid: expectedPid);
    }

    private (byte[] Pixels, int Width, int Height, long Version)? ReadRootFrame()
    {
        try
        {
            return _root.TryGetLatest();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && _root is SecondaryWindowsCaptureFallback popup)
        {
            throw new SecondaryWindowsCaptureException(popup.WindowHandle, ex);
        }
    }

    public string CaptureMode => _root.CaptureMode;

    public bool IsClosed => _closed || _root.IsClosed || !_isRootValid();

    public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsClosed)
            {
                return DrainClosedRoot();
            }

            try
            {
                return Sample();
            }
            catch (Exception ex) when (
                (ex is RootClosedException or Win32Exception) && IsClosed)
            {
                _logger.LogDebug(ex, "Captured root closed during secondary-window discovery; draining the final frame.");
                return DrainClosedRoot();
            }
        }
    }

    private (byte[] Pixels, int Width, int Height, long Version)? DrainClosedRoot()
    {
        if (!_closed)
        {
            _logger.LogDebug("Captured root is no longer valid; releasing secondary-window capture sessions.");
        }
        _closed = true;
        DisposeChildren();
        if (_root is SecondaryWindowsCaptureFallback)
        {
            return _latest;
        }
        var finalRoot = ReadRootFrame();
        if (finalRoot is not null && (_latest is null || finalRoot.Value.Version != _rootVersion))
        {
            _rootVersion = finalRoot.Value.Version;
            _latest = (finalRoot.Value.Pixels, finalRoot.Value.Width, finalRoot.Value.Height, ++_version);
        }
        return _latest;
    }

    private (byte[] Pixels, int Width, int Height, long Version)? Sample()
    {
        var popups = _discover();
        var handles = popups.Select(p => p.Handle).ToHashSet();
        foreach (var handle in _children.Keys.ToArray())
        {
            if (!handles.Contains(handle) || _children[handle].IsClosed)
            {
                _children[handle].Dispose();
                _children.Remove(handle);
                _firstFrameDeadlines.Remove(handle);
            }
        }
        if (popups.Count > 16)
        {
            throw new SecondaryWindowsCaptureException(popups[0].Handle,
                new InvalidOperationException("More than 16 intersecting secondary windows are visible."));
        }
        for (var i = popups.Count - 1; i >= 0; i--)
        {
            var popup = popups[i];
            if (!_children.ContainsKey(popup.Handle))
            {
                if (!_discover().Any(p => p.Handle == popup.Handle))
                {
                    _logger.LogDebug("Secondary window {Hwnd} disappeared before capture startup.", popup.Handle);
                    popups.RemoveAt(i);
                    continue;
                }
                try
                {
                    _children.Add(popup.Handle, _start(popup.Handle));
                    _firstFrameDeadlines.Add(popup.Handle, _clock() + 2000);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (_discover().Any(p => p.Handle == popup.Handle))
                    {
                        if (ex is WgcCapture.UnsupportedCaptureWindowException)
                        {
                            _children.Add(popup.Handle, StartPrintWindowFallback(new HWND(popup.Handle), _logger, ex, _expectedPid));
                            _firstFrameDeadlines.Add(popup.Handle, _clock() + 2000);
                            continue;
                        }
                        _logger.LogError(ex, "Windows graphics capture startup failed for still-visible secondary window {Hwnd}.", popup.Handle);
                        throw new SecondaryWindowsCaptureException(popup.Handle, ex);
                    }
                    _logger.LogDebug(ex, "Secondary window {Hwnd} disappeared during capture startup.", popup.Handle);
                    popups.RemoveAt(i);
                }
            }
        }

        var root = ReadRootFrame();
        if (root is null)
        {
            return null;
        }
        var bounds = _rootBounds();
        var result = root.Value;
        var inputs = new PopupInput[popups.Count];
        var childFrames = new (byte[] Pixels, int Width, int Height, long Version)[popups.Count];
        for (var i = 0; i < popups.Count; i++)
        {
            var grabber = _children[popups[i].Handle];
            (byte[] Pixels, int Width, int Height, long Version)? child;
            try
            {
                child = grabber.TryGetLatest();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_discover().Any(p => p.Handle == popups[i].Handle))
                {
                    _logger.LogDebug(ex, "Secondary window {Hwnd} disappeared during capture.", popups[i].Handle);
                    return null;
                }
                _logger.LogError(ex, "Capture failed for still-visible secondary window {Hwnd}.", popups[i].Handle);
                throw new SecondaryWindowsCaptureException(popups[i].Handle, ex);
            }
            if (child is null)
            {
                if (!_firstFrameDeadlines.TryGetValue(popups[i].Handle, out var deadline) ||
                    _clock() >= deadline)
                {
                    _logger.LogError("Secondary window {Hwnd} did not produce a capture frame within 2 seconds.", popups[i].Handle);
                    throw new SecondaryWindowsCaptureException(popups[i].Handle,
                        new TimeoutException($"Secondary window HWND {popups[i].Handle} did not produce a capture frame within 2 seconds."));
                }
                return null;
            }
            _firstFrameDeadlines.Remove(popups[i].Handle);
            childFrames[i] = child.Value;
            inputs[i] = new(popups[i], grabber, child.Value.Version, child.Value.Width, child.Value.Height);
        }
        // Revalidate ownership, handles, bounds and order before publishing cached or new pixels.
        if (!popups.SequenceEqual(_discover()))
        {
            return null;
        }
        if (_latest is not null && result.Version == _rootVersion &&
            result.Width == _latest.Value.Width && result.Height == _latest.Value.Height &&
            bounds == _bounds && inputs.SequenceEqual(_popupInputs))
        {
            return _latest;
        }

        var pixels = popups.Count == 0 ? result.Pixels : (byte[])result.Pixels.Clone();
        // Discovery is topmost-first. Paint the lowest owned popup first.
        for (var i = popups.Count - 1; i >= 0; i--)
        {
            var popup = popups[i];
            var child = childFrames[i];
            IncludeSecondaryWindowInWindowImage(pixels, result.Width, result.Height,
                child.Pixels, child.Width, child.Height,
                popup.Bounds.Left - bounds.Left, popup.Bounds.Top - bounds.Top);
        }
        _rootVersion = result.Version;
        _bounds = bounds;
        _popupInputs = inputs;
        _latest = (pixels, result.Width, result.Height, ++_version);
        return _latest;
    }

    public async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct)
        => await WaitForFrameAsync(timeout, ct).ConfigureAwait(false) is not null;

    public async Task<(byte[] Pixels, int Width, int Height, long Version)?> WaitForFrameAsync(
        TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        do
        {
            ct.ThrowIfCancellationRequested();
            var frame = TryGetLatest();
            if (frame is not null)
            {
                return frame;
            }
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                break;
            }
            await Task.Delay((int)Math.Min(30, remaining), ct).ConfigureAwait(false);
        }
        while (Environment.TickCount64 < deadline);
        ct.ThrowIfCancellationRequested();
        var finalFrame = TryGetLatest();
        if (finalFrame is not null)
        {
            return finalFrame;
        }
        lock (_lock)
        {
            if (!IsClosed && _root is SecondaryWindowsCaptureFallback popupRoot && ReadRootFrame() is null)
            {
                throw new SecondaryWindowsCaptureException(popupRoot.WindowHandle,
                    new TimeoutException("The targeted secondary window did not produce a frame before the capture deadline."));
            }
            if (!IsClosed && ReadRootFrame() is not null)
            {
                var popups = _discover();
                if (popups.Count != 0)
                {
                    var error = new TimeoutException("The secondary window did not produce a stable combined image before the capture deadline.");
                    _logger.LogError(error, "Secondary window {Hwnd} prevented the first combined image.", popups[0].Handle);
                    throw new SecondaryWindowsCaptureException(popups[0].Handle, error);
                }
            }
        }
        return null;
    }

    internal static bool IsOwnedBy(HWND candidate, HWND root, int pid)
    {
        var seen = new HashSet<nint>();
        while (!candidate.IsNull && seen.Add((nint)candidate))
        {
            if (RealOwnedWindowFinder.s_getWindowProcessId(candidate) != pid)
            {
                return false;
            }
            candidate = RealOwnedWindowFinder.s_getWindowOwner(candidate);
            if (candidate == root)
            {
                return true;
            }
        }
        return false;
    }

    internal static bool RootIsValid(HWND root, int pid)
        => IsWindow((nint)root) && RealOwnedWindowFinder.s_getWindowProcessId(root) == pid;

    internal static List<Popup> Discover(HWND root, int pid, ILogger logger,
        Func<HWND, PointerRect>? getBounds = null)
    {
        if (!RootIsValid(root, pid))
        {
            throw new RootClosedException();
        }
        getBounds ??= hwnd => GetBounds(hwnd, requireNonEmpty: false);
        var rootBounds = getBounds(root);
        if (rootBounds.Right <= rootBounds.Left || rootBounds.Bottom <= rootBounds.Top)
        {
            throw new InvalidOperationException("The captured root window has empty bounds.");
        }
        var result = new List<Popup>();
        var window = HWND.Null;
        while (!(window = RealOwnedWindowFinder.s_findNextTopLevelWindow(window)).IsNull)
        {
            if (window == root || !IsWindow((nint)window) ||
                !RealOwnedWindowFinder.s_isWindowVisible(window) || !IsOwnedBy(window, root, pid))
            {
                continue;
            }
            var style = (uint)GetWindowLong((nint)window, -16);
            var extended = (uint)GetWindowLong((nint)window, -20);
            const uint popupStyle = 0x80000000; // WS_POPUP
            const uint toolStyle = 0x00000080; // WS_EX_TOOLWINDOW
            if ((style & popupStyle) == 0 && (extended & toolStyle) == 0)
            {
                continue;
            }
            PointerRect bounds;
            try
            {
                bounds = getBounds(window);
            }
            catch (Win32Exception ex)
            {
                if (!IsWindow((nint)window) ||
                    !RealOwnedWindowFinder.s_isWindowVisible(window) || !IsOwnedBy(window, root, pid))
                {
                    logger.LogDebug(ex, "Secondary window {Hwnd} disappeared during its bounds query.", (nint)window);
                    continue;
                }
                logger.LogError(ex, "Cannot query bounds for still-visible secondary window {Hwnd}.", (nint)window);
                throw new SecondaryWindowsCaptureException((nint)window, ex);
            }
            if (bounds.Right > bounds.Left && bounds.Bottom > bounds.Top &&
                bounds.Left < rootBounds.Right && bounds.Right > rootBounds.Left &&
                bounds.Top < rootBounds.Bottom && bounds.Bottom > rootBounds.Top)
            {
                result.Add(new((nint)window, bounds));
            }
        }
        return result;
    }

    internal static unsafe PointerRect GetBounds(HWND hwnd, bool requireNonEmpty = true)
    {
        if (!PInvoke.GetWindowRect(hwnd, out var rect))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot query capture bounds for HWND {(nint)hwnd}.");
        }
        var visible = rect;
        var hr = PInvoke.DwmGetWindowAttribute(hwnd,
            global::Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &visible, (uint)sizeof(RECT));
        if (hr.Succeeded)
        {
            rect = visible;
        }
        if (requireNonEmpty && (rect.right <= rect.left || rect.bottom <= rect.top))
        {
            throw new InvalidOperationException("The capture window has empty bounds.");
        }
        return new(rect.left, rect.top, rect.right, rect.bottom);
    }

    // Preserve premultiplied BGRA transparency while clipping the UI image to the captured window.
    internal static void IncludeSecondaryWindowInWindowImage(byte[] root, int width, int height, byte[] popup,
        int popupWidth, int popupHeight, int left, int top)
    {
        if (root.Length != checked(width * height * 4) ||
            popup.Length != checked(popupWidth * popupHeight * 4))
        {
            throw new ArgumentException("Capture dimensions do not match the pixel buffers.");
        }

        var startX = Math.Max(0L, left);
        var startY = Math.Max(0L, top);
        var endX = Math.Min((long)width, (long)left + popupWidth);
        var endY = Math.Min((long)height, (long)top + popupHeight);
        for (var y = startY; y < endY; y++)
        {
            for (var x = startX; x < endX; x++)
            {
                var destination = checked((int)((y * width + x) * 4));
                var source = checked((int)(((y - top) * popupWidth + x - left) * 4));
                var inverseAlpha = 255 - popup[source + 3];
                for (var channel = 0; channel < 4; channel++)
                {
                    root[destination + channel] = (byte)Math.Min(255,
                        popup[source + channel] + (root[destination + channel] * inverseAlpha + 127) / 255);
                }
            }
        }
    }

    private void DisposeChildren()
    {
        foreach (var child in _children.Values)
        {
            child.Dispose();
        }

        _children.Clear();
        _firstFrameDeadlines.Clear();
    }

    private sealed class RootClosedException() : InvalidOperationException("The captured root window is no longer valid.");

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint hwnd, int index);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            DisposeChildren();
            _root.Dispose();
        }
    }
}

internal sealed class SecondaryWindowsCaptureException(nint hwnd, Exception cause)
    : InvalidOperationException($"Capture failed for secondary window HWND {hwnd}: {cause.Message}", cause);
#endif
