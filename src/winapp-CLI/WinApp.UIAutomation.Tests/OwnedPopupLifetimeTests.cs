// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class OwnedPopupLifetimeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FirstFrame_DeadlineIsBoundedAndDelayedFrameCanSucceed(bool arrives)
    {
        long now = 100;
        var root = new Grabber();
        var child = new Grabber { HasFrame = false };
        var popups = new List<OwnedPopupFrameGrabber.Popup>();
        var logger = new CaptureLogger();
        using var composite = new OwnedPopupFrameGrabber(root, () => new(0, 0, 1, 1),
            () => popups.ToList(), _ => child, () => now, logger: logger);
        var healthy = composite.TryGetLatest()!.Value;
        popups.Add(new(42, new(0, 0, 1, 1)));
        Assert.IsNull(composite.TryGetLatest());
        now = 2099;
        Assert.IsNull(composite.TryGetLatest());
        if (arrives)
        {
            child.HasFrame = true;
            var frame = composite.TryGetLatest()!.Value;
            Assert.AreEqual(healthy.Version + 1, frame.Version);
            now = 10000;
            Assert.AreEqual(frame.Version, composite.TryGetLatest()!.Value.Version);
        }
        else
        {
            now = 2100;
            var error = Assert.ThrowsExactly<TimeoutException>(() => composite.TryGetLatest());
            StringAssert.Contains(error.Message, "42");
            Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Error && m.Text.Contains("42")));
            popups.Clear();
            Assert.AreSame(root.Pixels, composite.TryGetLatest()!.Value.Pixels);
            Assert.IsTrue(child.Disposed);
        }
        composite.Dispose();
        Assert.IsTrue(root.Disposed);
        Assert.IsTrue(child.Disposed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Startup_DisappearingPopupIsSkippedBeforeOrDuringWgcStartup(bool duringStart)
    {
        var visible = true;
        var discoveries = 0;
        var starts = 0;
        var logger = new CaptureLogger();
        var root = new Grabber();
        using var composite = new OwnedPopupFrameGrabber(root, () => new(0, 0, 1, 1),
            () =>
            {
                if (!duringStart && ++discoveries > 1)
                {
                    visible = false;
                }
                return visible ? [new(42, new(0, 0, 1, 1))] : [];
            },
            _ =>
            {
                starts++;
                visible = false;
                throw new COMException("CreateForWindow", unchecked((int)0x80070057));
            }, logger: logger);
        Assert.AreSame(root.Pixels, composite.TryGetLatest()!.Value.Pixels);
        Assert.AreEqual(duringStart ? 1 : 0, starts);
        Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Debug && m.Text.Contains("42")));
    }

    [TestMethod]
    public void Startup_StillQualifyingPopupFailurePropagatesAndIsLogged()
    {
        var failure = new COMException("Still-valid CreateForWindow failure");
        var logger = new CaptureLogger();
        using var composite = new OwnedPopupFrameGrabber(new Grabber(), () => new(0, 0, 1, 1),
            () => [new(42, new(0, 0, 1, 1))], _ => throw failure, logger: logger);
        Assert.AreSame(failure, Assert.ThrowsExactly<COMException>(() => composite.TryGetLatest()));
        Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Error));
    }

    [TestMethod]
    public void Startup_ActualHwndDestructionDuringWgcInitializationContinuesRootCapture()
    {
        if (!WgcCapture.IsSupported() || ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native WGC startup requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        PopupWindow? popup = null;
        try
        {
            nint handle = 0;
            fixture.OnUiThread(() =>
            {
                popup = new PopupWindow();
                popup.Show(fixture.Form);
                handle = popup.Handle;
            });
            var rootHandle = new HWND(fixture.Hwnd);
            var childHandle = new HWND(handle);
            var pid = RealOwnedWindowFinder.s_getWindowProcessId(rootHandle);
            var logger = new CaptureLogger();
            var root = new Grabber();
            RealOwnedWindowFinder.s_findNextTopLevelWindow = after => after.IsNull ? childHandle : HWND.Null;
            using var composite = new OwnedPopupFrameGrabber(root, () => new(0, 0, 1, 1),
                () => OwnedPopupFrameGrabber.Discover(rootHandle, pid, logger, _ => new(0, 0, 10, 10)),
                hwnd =>
                {
                    fixture.OnUiThread(() => popup!.Dispose());
                    return WgcCapture.StartSingleWindowGrabber(new HWND(hwnd), NullLogger.Instance);
                }, logger: logger);
            Assert.AreSame(root.Pixels, composite.TryGetLatest()!.Value.Pixels);
            Assert.IsFalse(composite.IsClosed);
            Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Debug && m.Text.Contains("during capture startup")));
        }
        finally
        {
            RealOwnedWindowFinder.ResetNativeSeams();
            fixture.OnUiThread(() => popup?.Dispose());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RootClosure_DuringDiscoveryDrainsBeforeOrAfterWgcClosedEvent(bool nativeEvent)
    {
        var root = new Grabber();
        var child = new Grabber();
        var valid = true;
        var closeOnDiscovery = false;
        var logger = new CaptureLogger();
        using var composite = new OwnedPopupFrameGrabber(root, () => new(0, 0, 1, 1),
            () =>
            {
                if (closeOnDiscovery)
                {
                    valid = false;
                    root.IsClosed = nativeEvent;
                    return OwnedPopupFrameGrabber.Discover(HWND.Null, 0, logger);
                }
                return [new(42, new(0, 0, 1, 1))];
            }, _ => child, isRootValid: () => valid, logger: logger);
        composite.TryGetLatest();
        child.Version++;
        var healthy = composite.TryGetLatest()!.Value;
        closeOnDiscovery = true;
        var final = composite.TryGetLatest()!.Value;
        Assert.IsTrue(composite.IsClosed);
        Assert.IsTrue(child.Disposed);
        Assert.AreEqual(healthy.Version, final.Version);
        Assert.AreSame(healthy.Pixels, final.Pixels);
        Assert.AreEqual(final.Version, composite.TryGetLatest()!.Value.Version);
        Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Debug));
    }

    [TestMethod]
    public void RootClosure_InvalidBeforeSamplingDoesNotRequireWgcClosedEvent()
    {
        var valid = true;
        var root = new Grabber();
        using var composite = new OwnedPopupFrameGrabber(root, () => new(0, 0, 1, 1),
            () => [], _ => throw new AssertFailedException(), isRootValid: () => valid);
        var first = composite.TryGetLatest()!.Value;
        valid = false;
        Assert.IsTrue(composite.IsClosed);
        var final = composite.TryGetLatest()!.Value;
        Assert.AreEqual(first.Version, final.Version);
        Assert.AreSame(first.Pixels, final.Pixels);
    }

    [TestMethod]
    public void RootClosure_DuringRootBoundsQueryDrainsNativeQueryFailure()
    {
        var root = new Grabber();
        var valid = true;
        var failBounds = false;
        using var composite = new OwnedPopupFrameGrabber(root, () =>
            {
                if (failBounds)
                {
                    valid = false;
                    throw new Win32Exception(1400);
                }
                return new(0, 0, 1, 1);
            }, () => [], _ => throw new AssertFailedException(), isRootValid: () => valid);
        var first = composite.TryGetLatest()!.Value;
        failBounds = true;
        var final = composite.TryGetLatest()!.Value;
        Assert.IsTrue(composite.IsClosed);
        Assert.AreEqual(first.Version, final.Version);
        Assert.AreSame(first.Pixels, final.Pixels);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Discovery_UnexpectedErrorIsNotSuppressedEvenWhenRootCloses(bool close)
    {
        var root = new Grabber();
        var failure = new InvalidOperationException("Unexpected discovery defect");
        using var composite = new OwnedPopupFrameGrabber(root, () => new(0, 0, 1, 1),
            () =>
            {
                root.IsClosed = close;
                throw failure;
            }, _ => throw new AssertFailedException());
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => composite.TryGetLatest()));
    }

    [TestMethod]
    public void Discovery_UnrelatedComFailureIsNotSuppressedWhenRootCloses()
    {
        var root = new Grabber();
        var failure = new COMException("Unrelated discovery failure");
        using var composite = new OwnedPopupFrameGrabber(root, () => new(0, 0, 1, 1),
            () =>
            {
                root.IsClosed = true;
                throw failure;
            }, _ => throw new AssertFailedException());
        Assert.AreSame(failure, Assert.ThrowsExactly<COMException>(() => composite.TryGetLatest()));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Discovery_EmptyChildIsExcludedButEmptyRootIsFatal(bool emptyRoot)
    {
        WithOwnedPopup((root, popup, pid) =>
        {
            var results = new Func<List<OwnedPopupFrameGrabber.Popup>>(() =>
                OwnedPopupFrameGrabber.Discover(root, pid, NullLogger.Instance,
                    hwnd => hwnd == (emptyRoot ? root : popup) ? new(0, 0, 0, 0) : new(0, 0, 10, 10)));
            if (emptyRoot)
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => results());
            }
            else
            {
                Assert.AreEqual(0, results().Count);
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Discovery_FailedChildBoundsSkipsOnlyWhenChildDisappeared(bool disappears)
    {
        WithOwnedPopup((root, popup, pid) =>
        {
            var logger = new CaptureLogger();
            var failure = new Win32Exception(1400);
            var results = new Func<List<OwnedPopupFrameGrabber.Popup>>(() =>
                OwnedPopupFrameGrabber.Discover(root, pid, logger, hwnd =>
                {
                    if (hwnd == popup)
                    {
                        if (disappears)
                        {
                            RealOwnedWindowFinder.s_isWindowVisible = _ => false;
                        }
                        throw failure;
                    }
                    return new(0, 0, 10, 10);
                }));
            if (disappears)
            {
                Assert.AreEqual(0, results().Count);
                Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Debug));
            }
            else
            {
                Assert.AreSame(failure, Assert.ThrowsExactly<Win32Exception>(() => results()));
            }
        });
    }

    private static void WithOwnedPopup(Action<HWND, HWND, int> test)
    {
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native owned-popup discovery requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        PopupWindow? popup = null;
        try
        {
            nint handle = 0;
            fixture.OnUiThread(() =>
            {
                popup = new PopupWindow();
                popup.Show(fixture.Form);
                handle = popup.Handle;
            });
            var root = new HWND(fixture.Hwnd);
            var child = new HWND(handle);
            RealOwnedWindowFinder.s_findNextTopLevelWindow = after => after.IsNull ? child : HWND.Null;
            test(root, child, RealOwnedWindowFinder.s_getWindowProcessId(root));
        }
        finally
        {
            RealOwnedWindowFinder.ResetNativeSeams();
            fixture.OnUiThread(() => popup?.Dispose());
        }
    }

    private sealed class PopupWindow : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Style = unchecked((int)0x80000000);
                parameters.ExStyle |= 0x08000080;
                return parameters;
            }
        }
    }

    private sealed class Grabber : IFrameGrabber
    {
        internal byte[] Pixels { get; } = [0, 0, 255, 255];
        internal bool HasFrame { get; set; } = true;
        internal bool Disposed { get; private set; }
        internal long Version { get; set; } = 1;
        public bool IsClosed { get; set; }
        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
            => HasFrame ? (Pixels, 1, 1, Version) : null;
        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(HasFrame);
        public void Dispose() => Disposed = true;
    }

    private sealed class CaptureLogger : ILogger
    {
        internal List<(LogLevel Level, string Text)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception)));
    }
}
