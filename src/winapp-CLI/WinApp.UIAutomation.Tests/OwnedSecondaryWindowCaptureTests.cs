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
public class OwnedSecondaryWindowCaptureTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FirstFrame_DeadlineIsBoundedAndDelayedFrameCanSucceed(bool arrives)
    {
        long now = 100;
        var root = new Grabber();
        var child = new Grabber { HasFrame = false };
        var popups = new List<WindowCaptureIncludingOwnedSecondaryWindows.Popup>();
        var logger = new CaptureLogger();
        using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(root, () => new(0, 0, 1, 1),
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
            var error = Assert.ThrowsExactly<OwnedSecondaryWindowCaptureException>(() => composite.TryGetLatest());
            Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
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
        using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(root, () => new(0, 0, 1, 1),
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
    [DataRow(unchecked((int)0x80004005))]
    [DataRow(unchecked((int)0x80070005))]
    [DataRow(unchecked((int)0x80070057))]
    public void Startup_StillQualifyingPopupFailurePropagatesAndIsLogged(int hresult)
    {
        var failure = new COMException("Non-classified WGC startup failure", hresult);
        var logger = new CaptureLogger();
        using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(new Grabber(), () => new(0, 0, 1, 1),
            () => [new(42, new(0, 0, 1, 1))], _ => throw failure, logger: logger);
        Assert.AreSame(failure, Assert.ThrowsExactly<OwnedSecondaryWindowCaptureException>(() => composite.TryGetLatest()).InnerException);
        Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Error));
    }

    [TestMethod]
    [DataRow(0, 255, 0)]
    [DataRow(0, 0, 0)]
    public async Task Startup_InvalidArgumentUsesNativeWindowOnlyCaptureIncludingLegitimateBlack(int red, int green, int blue)
    {
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native PrintWindow requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        PopupWindow? popup = null;
        var screen = UiAutomationService.s_captureFromScreenScaled;
        var foreground = UiAutomationService.s_foregroundWindowForBlankRetry;
        var sleep = UiAutomationService.s_sleepForBlankRetry;
        try
        {
            nint handle = 0;
            fixture.OnUiThread(() =>
            {
                popup = new PopupWindow
                {
                    FormBorderStyle = FormBorderStyle.None,
                    BackColor = System.Drawing.Color.FromArgb(red, green, blue),
                    Size = new(32, 32)
                };
                popup.Show(fixture.Form);
                popup.Refresh();
                handle = popup.Handle;
            });
            UiAutomationService.s_captureFromScreenScaled = (_, _, _, _, _, _) => throw new AssertFailedException("Screen capture is forbidden.");
            UiAutomationService.s_foregroundWindowForBlankRetry = _ => Assert.Fail("Foreground retry is forbidden.");
            UiAutomationService.s_sleepForBlankRetry = _ => Assert.Fail("Blank retry is forbidden.");
            var visible = true;
            var starts = 0;
            var logger = new CaptureLogger();
            var bounds = WindowCaptureIncludingOwnedSecondaryWindows.GetBounds(new HWND(handle));
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            var root = new Grabber { Width = width, Height = height, Pixels = new byte[width * height * 4] };
            for (var i = 0; i < root.Pixels.Length; i += 4)
            {
                root.Pixels[i + 2] = 255;
                root.Pixels[i + 3] = 255;
            }
            using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(root, () => bounds,
                () => visible ? [new(handle, bounds)] : [],
                _ =>
                {
                    starts++;
                    WgcCapture.CheckCreateForWindowResult(new HWND(handle), unchecked((int)0x80070057));
                    throw new AssertFailedException("CreateForWindow failure must throw.");
                }, logger: logger);
            Assert.IsTrue(await composite.WaitForFirstFrameAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            var frame = await WaitForColor((byte)blue, (byte)green, (byte)red, -1);
            Assert.AreEqual(frame.Version, composite.TryGetLatest()!.Value.Version);
            Assert.AreEqual(1, starts);
            Assert.IsTrue(logger.Messages.Any(m => m.Level == LogLevel.Warning && m.Text.Contains("PrintWindow")));
            fixture.OnUiThread(() =>
            {
                popup!.BackColor = System.Drawing.Color.Blue;
                popup.Refresh();
            });
            var changed = await WaitForColor(255, 0, 0, frame.Version);
            Assert.IsTrue(changed.Version > frame.Version);
            visible = false;
            CollectionAssert.AreEqual(root.Pixels, composite.TryGetLatest()!.Value.Pixels);

            async Task<(byte[] Pixels, int Width, int Height, long Version)> WaitForColor(byte b, byte g, byte r, long after)
            {
                var deadline = Environment.TickCount64 + 2000;
                (byte[] Pixels, int Width, int Height, long Version) result = default;
                var offset = ((height / 2) * width + width / 2) * 4;
                do
                {
                    result = composite.TryGetLatest()!.Value;
                    if (result.Version > after && InteriorMatches(result.Pixels))
                    {
                        Assert.AreEqual(width, result.Width);
                        Assert.AreEqual(height, result.Height);
                        return result;
                    }
                    await Task.Delay(20);
                } while (Environment.TickCount64 < deadline);
                CollectionAssert.AreEqual(new byte[] { b, g, r, 255 }, result.Pixels[offset..(offset + 4)]);
                Assert.IsTrue(InteriorMatches(result.Pixels), "The central 8x8 physical popup pixels must all match the expected opaque color.");
                Assert.Fail("Popup frame version did not advance.");
                return result;

                bool InteriorMatches(byte[] pixels)
                {
                    for (var y = height / 2 - 4; y < height / 2 + 4; y++)
                    {
                        for (var x = width / 2 - 4; x < width / 2 + 4; x++)
                        {
                            var index = (y * width + x) * 4;
                            if (pixels[index] != b || pixels[index + 1] != g ||
                                pixels[index + 2] != r || pixels[index + 3] != 255)
                            {
                                return false;
                            }
                        }
                    }
                    return true;
                }
            }
        }
        finally
        {
            UiAutomationService.s_captureFromScreenScaled = screen;
            UiAutomationService.s_foregroundWindowForBlankRetry = foreground;
            UiAutomationService.s_sleepForBlankRetry = sleep;
            fixture.OnUiThread(() => popup?.Dispose());
        }
    }

    [TestMethod]
    public void PrintWindow_InvalidWindowFailsExplicitly()
    {
        Assert.ThrowsExactly<Win32Exception>(() => UiAutomationService.RenderOwnedSecondaryWindowForCapture(new HWND(-1), 2, 2));
    }

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 0)]
    [DataRow(true, 0x5A)]
    [DataRow(true, 0xA5)]
    [DataRow(true, -1)]
    public void PrintWindow_RejectsPartialPaintingButAcceptsBlackAndGenuineSeedColors(bool full, int shade)
    {
        WithOwnedPopup((_, window, _) =>
        {
            var attempts = 0;
            byte[] Capture() => UiAutomationService.RenderOwnedSecondaryWindowForCapture(window, 16, 16, (_, dc) =>
            {
                attempts++;
                for (var y = 0; y < (full ? 16 : 1); y++)
                {
                    for (var x = 0; x < (full ? 16 : 1); x++)
                    {
                        var value = shade < 0 ? ((x + y) % 2 == 0 ? 0x5Au : 0xA5u) : (uint)shade;
                        Assert.AreNotEqual(uint.MaxValue, SetPixel(dc, x, y, value * 0x010101u));
                    }
                }
                return true;
            });
            if (!full)
            {
                var failure = Assert.ThrowsExactly<InvalidOperationException>(() => Capture());
                StringAssert.Contains(failure.Message, "did not paint all pixels");
                Assert.AreEqual(2, attempts);
                return;
            }
            var pixels = Capture();
            Assert.AreEqual(16 * 16 * 4, pixels.Length);
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var value = shade < 0 ? ((x + y) % 2 == 0 ? (byte)0x5A : (byte)0xA5) : (byte)shade;
                    var index = (y * 16 + x) * 4;
                    CollectionAssert.AreEqual(new byte[] { value, value, value, 255 }, pixels[index..(index + 4)]);
                }
            }
            Assert.AreEqual(shade is 0x5A or -1 ? 2 : 1, attempts);
        });
    }

    [DllImport("gdi32.dll")]
    private static extern uint SetPixel(global::Windows.Win32.Graphics.Gdi.HDC dc, int x, int y, uint color);

    [TestMethod]
    [DataRow(1u)]
    [DataRow(0x11u)]
    public void PrintWindow_DisplayAffinityProtectionIsDenied(uint affinity)
    {
        WithOwnedPopup((_, child, _) =>
        {
            Assert.IsTrue(SetWindowDisplayAffinity(child, affinity), $"Cannot set test affinity: {Marshal.GetLastPInvokeError()}.");
            try
            {
                var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    OwnedSecondaryWindowCaptureFallback.EnsureCaptureAllowed(child));
                StringAssert.Contains(failure.Message, "protected from capture");
            }
            finally
            {
                Assert.IsTrue(SetWindowDisplayAffinity(child, 0));
            }
        });
    }

    [TestMethod]
    public void PrintWindow_ConsumedFrameStartsNextRenderWithoutAnotherPoll()
    {
        WithOwnedPopup((_, child, _) =>
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            long now = 0;
            var calls = 0;
            using var grabber = new OwnedSecondaryWindowCaptureFallback(child, () =>
            {
                var value = Interlocked.Increment(ref calls);
                if (value == 2)
                {
                    started.Set();
                    try
                    {
                        Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)));
                    }
                    finally
                    {
                        finished.Set();
                    }
                }
                return (new byte[] { (byte)value, 0, 0, 255 }, 1, 1);
            }, () => now);
            try
            {
                var deadline = Environment.TickCount64 + 2000;
                var frame = grabber.TryGetLatest();
                while (frame is null && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(5);
                    frame = grabber.TryGetLatest();
                }
                Assert.IsNotNull(frame);
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(2)),
                    "Publishing the first frame must start the replacement render without another sample.");
                Assert.AreEqual(2, calls);
                now = 1999;
                Assert.AreEqual(frame.Value.Version, grabber.TryGetLatest()!.Value.Version);
                Assert.AreEqual(2, calls, "Sampling must not start a second concurrent render.");
                now = 2000;
                Assert.ThrowsExactly<TimeoutException>(() => grabber.TryGetLatest());
            }
            finally
            {
                release.Set();
                if (started.IsSet)
                {
                    Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(2)));
                }
            }
        });
    }

    [TestMethod]
    public void PrintWindow_SlowNativeCallDoesNotBlockSamplingAndTimesOut()
    {
        WithOwnedPopup((_, child, _) =>
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            long now = 0;
            var calls = 0;
            using var grabber = new OwnedSecondaryWindowCaptureFallback(child, () =>
            {
                Interlocked.Increment(ref calls);
                started.Set();
                release.Wait();
                finished.Set();
                return (new byte[] { 0, 0, 0, 255 }, 1, 1);
            }, () => now);
            try
            {
                Assert.IsNull(grabber.TryGetLatest());
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(2)));
                now = 1999;
                Assert.IsNull(grabber.TryGetLatest());
                now = 2000;
                Assert.ThrowsExactly<TimeoutException>(() => grabber.TryGetLatest());
                Assert.AreEqual(1, calls);
            }
            finally
            {
                release.Set();
                Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(2)));
            }
        });
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(HWND window, uint affinity);

    [TestMethod]
    public async Task PrintWindow_FramedWindowUsesDwmDimensionsAndCorrectClientPixelPosition()
    {
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native PrintWindow requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        Form? popup = null;
        try
        {
            nint handle = 0;
            fixture.OnUiThread(() =>
            {
                popup = new Form
                {
                    BackColor = System.Drawing.Color.Lime,
                    Size = new(160, 140),
                    ShowInTaskbar = false
                };
                popup.Show(fixture.Form);
                popup.Refresh();
                handle = popup.Handle;
            });
            using var grabber = new OwnedSecondaryWindowCaptureFallback(new HWND(handle));
            Assert.IsTrue(await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            var frame = grabber.TryGetLatest()!.Value;
            var renderDeadline = Environment.TickCount64 + 2000;
            while (!HasGreenPixels(frame.Pixels) && Environment.TickCount64 < renderDeadline)
            {
                await Task.Delay(20);
                frame = grabber.TryGetLatest()!.Value;
            }
            var previous = SetThreadDpiAwarenessContext(-4);
            Assert.AreNotEqual((nint)0, previous);
            try
            {
                var bounds = WindowCaptureIncludingOwnedSecondaryWindows.GetBounds(new HWND(handle));
                Assert.AreEqual(bounds.Right - bounds.Left, frame.Width);
                Assert.AreEqual(bounds.Bottom - bounds.Top, frame.Height);
                var clientOrigin = new System.Drawing.Point();
                Assert.IsTrue(ClientToScreen(new HWND(handle), ref clientOrigin));
                var offset = ((clientOrigin.Y - bounds.Top + 10) * frame.Width +
                    clientOrigin.X - bounds.Left + 10) * 4;
                CollectionAssert.AreEqual(new byte[] { 0, 255, 0, 255 }, frame.Pixels[offset..(offset + 4)],
                    $"bounds={bounds}, client={clientOrigin}, size={frame.Width}x{frame.Height}");
            }
            finally
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }
        finally
        {
            fixture.OnUiThread(() => popup?.Dispose());
        }

        static bool HasGreenPixels(byte[] pixels)
        {
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] == 0 && pixels[i + 1] == 255 && pixels[i + 2] == 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Sampling_ChildFailureIsHarmlessOnlyIfChildDisappeared(bool disappears)
    {
        var visible = true;
        var failure = new Win32Exception(1400);
        using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(new Grabber(), () => new(0, 0, 1, 1),
            () => visible ? [new(42, new(0, 0, 1, 1))] : [],
            _ => new Grabber
            {
                OnSample = () =>
                {
                    visible = !disappears;
                    throw failure;
                }
            });
        if (disappears)
        {
            Assert.IsNull(composite.TryGetLatest());
            CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255 }, composite.TryGetLatest()!.Value.Pixels);
        }
        else
        {
            Assert.AreSame(failure, Assert.ThrowsExactly<OwnedSecondaryWindowCaptureException>(() => composite.TryGetLatest()).InnerException);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(HWND window, ref System.Drawing.Point point);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PrintWindow_NativeFailureOrUnpaintedBitmapFailsExplicitly(bool reportsSuccess)
    {
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native PrintWindow requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        PopupWindow? popup = null;
        try
        {
            nint handle = 0;
            fixture.OnUiThread(() =>
            {
                popup = new PopupWindow { FormBorderStyle = FormBorderStyle.None };
                popup.Show(fixture.Form);
                handle = popup.Handle;
            });
            var attempts = 0;
            byte[] Capture() => UiAutomationService.RenderOwnedSecondaryWindowForCapture(new HWND(handle), 16, 16,
                (_, _) => { attempts++; return reportsSuccess; });
            if (reportsSuccess)
            {
                var failure = Assert.ThrowsExactly<InvalidOperationException>(() => Capture());
                StringAssert.Contains(failure.Message, "did not paint");
                Assert.AreEqual(2, attempts);
            }
            else
            {
                var failure = Assert.ThrowsExactly<Win32Exception>(() => Capture());
                StringAssert.Contains(failure.Message, "PrintWindow");
                Assert.AreEqual(1, attempts);
            }
        }
        finally
        {
            fixture.OnUiThread(() => popup?.Dispose());
        }
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
            using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(root, () => new(0, 0, 1, 1),
                () => WindowCaptureIncludingOwnedSecondaryWindows.Discover(rootHandle, pid, logger, _ => new(0, 0, 10, 10)),
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
        using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(root, () => new(0, 0, 1, 1),
            () =>
            {
                if (closeOnDiscovery)
                {
                    valid = false;
                    root.IsClosed = nativeEvent;
                    return WindowCaptureIncludingOwnedSecondaryWindows.Discover(HWND.Null, 0, logger);
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
        using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(root, () => new(0, 0, 1, 1),
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
        using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(root, () =>
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
        using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(root, () => new(0, 0, 1, 1),
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
        using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(root, () => new(0, 0, 1, 1),
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
            var results = new Func<List<WindowCaptureIncludingOwnedSecondaryWindows.Popup>>(() =>
                WindowCaptureIncludingOwnedSecondaryWindows.Discover(root, pid, NullLogger.Instance,
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
            var results = new Func<List<WindowCaptureIncludingOwnedSecondaryWindows.Popup>>(() =>
                WindowCaptureIncludingOwnedSecondaryWindows.Discover(root, pid, logger, hwnd =>
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
                Assert.AreSame(failure, Assert.ThrowsExactly<OwnedSecondaryWindowCaptureException>(() => results()).InnerException);
            }
        });
    }

    [TestMethod]
    public async Task FirstFrame_OverallDeadlineDistinguishesPendingPopupFromMissingRootFrame()
    {
        using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(new Grabber(), () => new(0, 0, 1, 1),
            () => [new(42, new(0, 0, 1, 1))], _ => new Grabber { HasFrame = false }, () => 100);
        var failure = await Assert.ThrowsExactlyAsync<OwnedSecondaryWindowCaptureException>(() =>
            composite.WaitForFirstFrameAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.IsInstanceOfType<TimeoutException>(failure.InnerException);

        using var missingRoot = new WindowCaptureIncludingOwnedSecondaryWindows(new Grabber { HasFrame = false },
            () => new(0, 0, 1, 1), () => [], _ => throw new AssertFailedException());
        Assert.IsFalse(await missingRoot.WaitForFirstFrameAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TargetedOwnedToolPopup_ItemRejectionCapturesExactPixelsForScreenshotAndRecordingStartup(bool recording)
    {
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native popup capture requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        PopupWindow? popup = null;
        var start = WgcCapture.s_startGrabber;
        var screen = UiAutomationService.s_captureFromScreenScaled;
        var foreground = UiAutomationService.s_foregroundWindowForBlankRetry;
        var blankRetry = UiAutomationService.s_sleepForBlankRetry;
        IFrameGrabber? grabber = null;
        try
        {
            nint handle = 0;
            fixture.OnUiThread(() =>
            {
                popup = new PopupWindow
                {
                    FormBorderStyle = FormBorderStyle.None,
                    Size = new(48, 48),
                    BackColor = System.Drawing.Color.Lime
                };
                popup.Show(fixture.Form);
                popup.Refresh();
                handle = popup.Handle;
            });
            var starts = 0;
            WgcCapture.s_startGrabber = (window, logger, fps) => WindowCaptureIncludingOwnedSecondaryWindows.Start(window, logger, fps,
                (candidate, _, rate) =>
                {
                    Assert.AreEqual(handle, (nint)candidate);
                    Assert.AreEqual(recording ? 15 : 0, rate);
                    starts++;
                    WgcCapture.CheckCreateForWindowResult(candidate, unchecked((int)0x80070057));
                    throw new AssertFailedException();
                });
            UiAutomationService.s_captureFromScreenScaled = (_, _, _, _, _, _) => throw new AssertFailedException("Screen capture is forbidden.");
            UiAutomationService.s_foregroundWindowForBlankRetry = _ => Assert.Fail("Foreground retry is forbidden.");
            UiAutomationService.s_sleepForBlankRetry = _ => Assert.Fail("Blank retry is forbidden.");
            if (recording)
            {
                var backend = new WgcWindowCapture(NullLogger<WgcWindowCapture>.Instance);
                grabber = backend.StartFrameGrabber(handle, 15);
                Assert.IsTrue(await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            }
            var bounds = WindowCaptureIncludingOwnedSecondaryWindows.GetBounds(new HWND(handle));
            var deadline = Environment.TickCount64 + 2000;
            byte[] pixels;
            int width;
            int height;
            do
            {
                if (grabber is null)
                {
                    (pixels, width, height) = await WgcCapture.CaptureAsync(new HWND(handle), NullLogger.Instance, CancellationToken.None);
                }
                else
                {
                    var frame = grabber.TryGetLatest()!.Value;
                    (pixels, width, height) = (frame.Pixels, frame.Width, frame.Height);
                }
                if (InteriorIsGreen(pixels, width, height))
                {
                    break;
                }
                await Task.Delay(20);
            } while (Environment.TickCount64 < deadline);
            Assert.AreEqual(bounds.Right - bounds.Left, width);
            Assert.AreEqual(bounds.Bottom - bounds.Top, height);
            Assert.IsTrue(InteriorIsGreen(pixels, width, height), "All central 8x8 physical pixels must be opaque lime.");
            Assert.IsTrue(starts > 0);
            if (recording)
            {
                Assert.AreEqual(1, starts);
                fixture.OnUiThread(() => popup!.Close());
                Assert.IsTrue(grabber!.IsClosed);
                Assert.IsNotNull(grabber.TryGetLatest());
            }
        }
        finally
        {
            grabber?.Dispose();
            WgcCapture.s_startGrabber = start;
            UiAutomationService.s_captureFromScreenScaled = screen;
            UiAutomationService.s_foregroundWindowForBlankRetry = foreground;
            UiAutomationService.s_sleepForBlankRetry = blankRetry;
            fixture.OnUiThread(() => popup?.Dispose());
        }

        static bool InteriorIsGreen(byte[] pixels, int width, int height)
        {
            for (var y = height / 2 - 4; y < height / 2 + 4; y++)
            {
                for (var x = width / 2 - 4; x < width / 2 + 4; x++)
                {
                    var index = (y * width + x) * 4;
                    if (pixels[index] != 0 || pixels[index + 1] != 255 || pixels[index + 2] != 0 || pixels[index + 3] != 255)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void TargetedRoot_OrdinaryWindowRejectionAndGenericPopupInvalidArgumentStillPropagate(int kind)
    {
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Native popup classification requires an interactive desktop.");
        }
        using var fixture = new UiaTestFixture();
        PopupWindow? popup = null;
        try
        {
            var window = new HWND(fixture.Hwnd);
            if (kind != 0)
            {
                fixture.OnUiThread(() =>
                {
                    popup = new PopupWindow { FormBorderStyle = FormBorderStyle.None };
                    if (kind == 2)
                    {
                        popup.Show(fixture.Form);
                    }
                    else
                    {
                        popup.Show();
                    }
                    window = new HWND(popup.Handle);
                });
            }
            COMException failure = kind == 2
                ? new COMException("Device conversion E_INVALIDARG", unchecked((int)0x80070057))
                : new WgcCapture.UnsupportedCaptureWindowException(window);
            if (kind == 2)
            {
                Assert.AreSame(failure, Assert.ThrowsExactly<COMException>(() =>
                    WindowCaptureIncludingOwnedSecondaryWindows.Start(window, NullLogger.Instance, 0, (_, _, _) => throw failure)));
            }
            else
            {
                Assert.AreSame(failure, Assert.ThrowsExactly<WgcCapture.UnsupportedCaptureWindowException>(() =>
                    WindowCaptureIncludingOwnedSecondaryWindows.Start(window, NullLogger.Instance, 0, (_, _, _) => throw failure)));
            }
        }
        finally
        {
            fixture.OnUiThread(() => popup?.Dispose());
        }
    }

    [TestMethod]
    public void RootClosure_StrictGdiDrainsCachedFrameWithoutSchedulingAnotherCapture()
    {
        WithOwnedPopup((_, child, _) =>
        {
            var calls = 0;
            var valid = true;
            using var root = new OwnedSecondaryWindowCaptureFallback(child,
                () => (new byte[] { (byte)Interlocked.Increment(ref calls), 0, 0, 255 }, 1, 1));
            using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(root, () => new(0, 0, 1, 1),
                () => [], _ => throw new AssertFailedException(), isRootValid: () => valid);
            var deadline = Environment.TickCount64 + 2000;
            var healthy = composite.TryGetLatest();
            while (healthy is null && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(5);
                healthy = composite.TryGetLatest();
            }
            Assert.IsNotNull(healthy);
            Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 2, TimeSpan.FromSeconds(2)),
                "One replacement render is already scheduled while the root is still valid.");
            Assert.IsFalse(root.IsClosed, "The native HWND remains visible while the compositor identity is invalidated.");
            valid = false;
            Assert.IsTrue(composite.IsClosed);
            var final = composite.TryGetLatest()!.Value;
            Assert.AreEqual(2, calls);
            Assert.AreEqual(healthy.Value.Version, final.Version);
            Assert.AreSame(healthy.Value.Pixels, final.Pixels);
            Assert.AreEqual(final.Version, composite.TryGetLatest()!.Value.Version);
            Assert.AreEqual(2, calls);
        });
    }

    [TestMethod]
    public void PrintWindow_CompletedResultFromChangedPidIsDiscardedWithoutAdvancingCache()
    {
        WithOwnedPopup((_, child, pid) =>
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var calls = 0;
            var getPid = RealOwnedWindowFinder.s_getWindowProcessId;
            using var root = new OwnedSecondaryWindowCaptureFallback(child, () =>
            {
                var value = Interlocked.Increment(ref calls);
                if (value == 2)
                {
                    started.Set();
                    release.Wait();
                    finished.Set();
                }
                return (new byte[] { (byte)value, 0, 0, 255 }, 1, 1);
            });
            try
            {
                var deadline = Environment.TickCount64 + 2000;
                var healthy = root.TryGetLatest();
                while (healthy is null && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(5);
                    healthy = root.TryGetLatest();
                }
                Assert.IsNotNull(healthy);
                root.TryGetLatest();
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(2)));
                RealOwnedWindowFinder.s_getWindowProcessId = window => window == child ? pid + 1 : getPid(window);
                release.Set();
                Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(2)));
                var final = root.TryGetLatest()!.Value;
                Assert.IsTrue(root.IsClosed);
                Assert.AreEqual(healthy.Value.Version, final.Version);
                Assert.AreSame(healthy.Value.Pixels, final.Pixels);
                Assert.AreEqual((byte)1, final.Pixels[0]);
                root.TryGetLatest();
                Assert.AreEqual(2, calls);
            }
            finally
            {
                release.Set();
                if (started.IsSet)
                {
                    Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(2)));
                }
                RealOwnedWindowFinder.s_getWindowProcessId = getPid;
            }
        });
    }

    [TestMethod]
    public void RootClosure_WgcStyleFinalFrameStillDrainsAndAdvancesVersion()
    {
        var reads = 0;
        var valid = true;
        var root = new Grabber { OnSample = () => reads++ };
        using var composite = new WindowCaptureIncludingOwnedSecondaryWindows(root, () => new(0, 0, 1, 1),
            () => [], _ => throw new AssertFailedException(), isRootValid: () => valid);
        var healthy = composite.TryGetLatest()!.Value;
        Assert.AreEqual(1, reads);
        root.Version++;
        root.Pixels[0] = 42;
        valid = false;
        var final = composite.TryGetLatest()!.Value;
        Assert.AreEqual(2, reads);
        Assert.AreEqual(healthy.Version + 1, final.Version);
        Assert.AreEqual((byte)42, final.Pixels[0]);
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
        internal byte[] Pixels { get; init; } = [0, 0, 255, 255];
        internal int Width { get; init; } = 1;
        internal int Height { get; init; } = 1;
        internal bool HasFrame { get; set; } = true;
        internal bool Disposed { get; private set; }
        internal long Version { get; set; } = 1;
        internal Action? OnSample { get; init; }
        public bool IsClosed { get; set; }
        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
        {
            OnSample?.Invoke();
            return HasFrame ? (Pixels, Width, Height, Version) : null;
        }
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
