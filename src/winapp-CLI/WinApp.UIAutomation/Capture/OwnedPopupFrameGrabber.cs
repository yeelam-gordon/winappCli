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

internal sealed partial class OwnedPopupFrameGrabber : IFrameGrabber
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

    internal OwnedPopupFrameGrabber(IFrameGrabber root, Func<PointerRect> rootBounds,
        Func<List<Popup>> discover, Func<nint, IFrameGrabber> start,
        Func<long>? clock = null, Func<bool>? isRootValid = null, ILogger? logger = null)
    {
        _root = root;
        _rootBounds = rootBounds;
        _discover = discover;
        _start = start;
        _clock = clock ?? (() => Environment.TickCount64);
        _isRootValid = isRootValid ?? (() => true);
        _logger = logger ?? NullLogger.Instance;
    }

    internal static OwnedPopupFrameGrabber Start(HWND hwnd, ILogger logger, int fps)
    {
        var pid = RealOwnedWindowFinder.s_getWindowProcessId(hwnd);
        return new(WgcCapture.StartSingleWindowGrabber(hwnd, logger, fps),
            () => GetBounds(hwnd), () => Discover(hwnd, pid, logger),
            child => WgcCapture.StartSingleWindowGrabber(new HWND(child), logger, fps),
            isRootValid: () => RootIsValid(hwnd, pid), logger: logger);
    }

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
                _logger.LogDebug(ex, "Captured root closed during popup discovery; draining the final frame.");
                return DrainClosedRoot();
            }
        }
    }

    private (byte[] Pixels, int Width, int Height, long Version)? DrainClosedRoot()
    {
        if (!_closed)
        {
            _logger.LogDebug("Captured root is no longer valid; releasing owned-popup sessions.");
        }
        _closed = true;
        DisposeChildren();
        var finalRoot = _root.TryGetLatest();
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
            throw new OwnedPopupCaptureException(popups[0].Handle,
                new InvalidOperationException("More than 16 intersecting owned popups are visible."));
        }
        for (var i = popups.Count - 1; i >= 0; i--)
        {
            var popup = popups[i];
            if (!_children.ContainsKey(popup.Handle))
            {
                if (!_discover().Any(p => p.Handle == popup.Handle))
                {
                    _logger.LogDebug("Owned popup {Hwnd} disappeared before capture startup.", popup.Handle);
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
                            _logger.LogWarning(ex, "WGC rejected owned popup {Hwnd}; using window-only PrintWindow capture.", popup.Handle);
                            _children.Add(popup.Handle, new PrintWindowPopupFrameGrabber(new HWND(popup.Handle), logger: _logger));
                            _firstFrameDeadlines.Add(popup.Handle, _clock() + 2000);
                            continue;
                        }
                        _logger.LogError(ex, "WGC startup failed for still-visible owned popup {Hwnd}.", popup.Handle);
                        throw new OwnedPopupCaptureException(popup.Handle, ex);
                    }
                    _logger.LogDebug(ex, "Owned popup {Hwnd} disappeared during capture startup.", popup.Handle);
                    popups.RemoveAt(i);
                }
            }
        }

        var root = _root.TryGetLatest();
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
                    _logger.LogDebug(ex, "Owned popup {Hwnd} disappeared during capture.", popups[i].Handle);
                    return null;
                }
                _logger.LogError(ex, "Capture failed for still-visible owned popup {Hwnd}.", popups[i].Handle);
                throw new OwnedPopupCaptureException(popups[i].Handle, ex);
            }
            if (child is null)
            {
                if (!_firstFrameDeadlines.TryGetValue(popups[i].Handle, out var deadline) ||
                    _clock() >= deadline)
                {
                    _logger.LogError("Owned popup {Hwnd} did not produce a capture frame within 2 seconds.", popups[i].Handle);
                    throw new OwnedPopupCaptureException(popups[i].Handle,
                        new TimeoutException($"Owned popup HWND {popups[i].Handle} did not produce a capture frame within 2 seconds."));
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
            OwnedPopupComposition.Blend(pixels, result.Width, result.Height,
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
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        do
        {
            ct.ThrowIfCancellationRequested();
            if (TryGetLatest() is not null)
            {
                return true;
            }
            await Task.Delay(30, ct).ConfigureAwait(false);
        }
        while (Environment.TickCount64 < deadline);
        if (TryGetLatest() is not null)
        {
            return true;
        }
        lock (_lock)
        {
            if (!IsClosed && _root.TryGetLatest() is not null)
            {
                var popups = _discover();
                if (popups.Count != 0)
                {
                    var error = new TimeoutException("The owned popup did not produce a stable composite frame before the capture deadline.");
                    _logger.LogError(error, "Owned popup {Hwnd} prevented the first composite frame.", popups[0].Handle);
                    throw new OwnedPopupCaptureException(popups[0].Handle, error);
                }
            }
        }
        return false;
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

    private static bool RootIsValid(HWND root, int pid)
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
                    logger.LogDebug(ex, "Owned popup {Hwnd} disappeared during its bounds query.", (nint)window);
                    continue;
                }
                logger.LogError(ex, "Cannot query bounds for still-visible owned popup {Hwnd}.", (nint)window);
                throw new OwnedPopupCaptureException((nint)window, ex);
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

internal sealed class OwnedPopupCaptureException(nint hwnd, Exception cause)
    : InvalidOperationException($"Capture failed for owned popup HWND {hwnd}: {cause.Message}", cause);
#endif
