// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class WindowCaptureSessionIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(30000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Recording_PopupOpacityMatchesTheVisibleBlend(bool perPixel)
    {
        if (!WgcCapture.IsSupported() || ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Popup opacity requires WGC and an unlocked interactive desktop.");
        }

        using var fx = new UiaTestFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        PopupWindow? popup = null;
        nint popupHandle = 0;
        try
        {
            fx.OnUiThread(() =>
            {
                foreach (Control control in fx.Form.Controls)
                {
                    control.Hide();
                }
                fx.Form.BackColor = System.Drawing.Color.Red;
                fx.Form.TopMost = true;
                var origin = fx.Form.PointToScreen(System.Drawing.Point.Empty);
                popup = new PopupWindow
                {
                    Bounds = new System.Drawing.Rectangle(origin.X + 30, origin.Y + 50, 100, 90),
                    Opacity = perPixel ? 1 : 128d / 255,
                };
                popup.Show(fx.Form);
                popup.Refresh();
                if (perPixel)
                {
                    popup.SetPerPixelOpacity();
                }
                popupHandle = popup.Handle;
            });

            WgcCapture.FrameGrabber raw;
            try
            {
                raw = WgcCapture.StartSingleWindowGrabber(new HWND(popupHandle), NullLogger.Instance);
            }
            catch (WgcCapture.UnsupportedCaptureWindowException)
            {
                Assert.Inconclusive("This Windows build rejects direct popup WGC. PrintWindow cannot validate per-pixel transparency.");
                throw;
            }
            using var rawCapture = raw;
            Assert.IsTrue(await raw.WaitForFirstFrameAsync(TimeSpan.FromSeconds(3), timeout.Token));
            var rawFrame = raw.TryGetLatest()!.Value;
            var rawCenter = ((rawFrame.Height / 2) * rawFrame.Width + rawFrame.Width / 2) * 4;
            TestContext.WriteLine($"Raw WGC popup BGRA: {string.Join(',', rawFrame.Pixels[rawCenter..(rawCenter + 4)])}");
            CollectionAssert.AreEqual(perPixel ? new byte[] { 0, 128, 0, 128 } : [0, 255, 0, 255],
                rawFrame.Pixels[rawCenter..(rawCenter + 4)], "Only per-pixel alpha is already present in WGC pixels.");
            var hasAttributes = GetLayeredWindowAttributes(popupHandle, out _, out var alpha, out var flags);
            TestContext.WriteLine($"Layered attributes: success={hasAttributes}, alpha={alpha}, flags={flags}");
            Assert.AreEqual(!perPixel, hasAttributes, "UpdateLayeredWindow must exercise the no-global-alpha-attribute path.");

            using var grabber = WgcCapture.StartGrabber(new HWND(fx.Hwnd), NullLogger.Instance, 30);
            Assert.IsTrue(await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(3), timeout.Token));
            await WaitForBlendAsync(128);
            if (!perPixel)
            {
                foreach (var opacity in new byte[] { 64, 255, 0, 128 })
                {
                    fx.OnUiThread(() => popup!.Opacity = opacity / 255d);
                    await WaitForBlendAsync(opacity);
                }
            }

            async Task WaitForBlendAsync(byte opacity)
            {
                var deadline = Environment.TickCount64 + 3000;
                var matchingPixels = 0;
                do
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var frame = grabber.TryGetLatest();
                    matchingPixels = 0;
                    if (frame is { } value)
                    {
                        for (var i = 0; i < value.Pixels.Length; i += 4)
                        {
                            if (value.Pixels[i] <= 2 && Math.Abs(value.Pixels[i + 1] - opacity) <= 2 &&
                                Math.Abs(value.Pixels[i + 2] - (255 - opacity)) <= 2 && value.Pixels[i + 3] == 255)
                            {
                                matchingPixels++;
                            }
                        }
                    }
                    if (matchingPixels >= 1000)
                    {
                        return;
                    }
                    await Task.Delay(50, timeout.Token);
                }
                while (Environment.TickCount64 < deadline);
                Assert.Fail($"Green popup over red with opacity {opacity} must blend to BGRA 0,{opacity},{255 - opacity},255; found {matchingPixels} matching pixels.");
            }
        }
        finally
        {
            fx.OnUiThread(() => popup?.Dispose());
        }
    }

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 0)]
    [DataRow(true, 15)]
    [DataRow(true, 1)]
    public async Task Capture_RecordingIncludesOpenPopupButScreenshotCapturesWindowAlone(bool continuous, int fps)
    {
        if (!WgcCapture.IsSupported())
        {
            Assert.Inconclusive("Owned-popup capture requires WGC.");
        }
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Popup rendering requires an unlocked interactive desktop.");
        }

        using var fx = new UiaTestFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        PopupWindow? popup = null;
        IFrameGrabber? grabber = null;
        long lastMatchedVersion = -1;
        try
        {
            fx.OnUiThread(() =>
            {
                foreach (Control control in fx.Form.Controls)
                {
                    control.Hide();
                }
                fx.Form.BackColor = System.Drawing.Color.Red;
                fx.Form.TopMost = true;
            });

            if (continuous)
            {
                grabber = WgcCapture.StartGrabber(new HWND(fx.Hwnd), NullLogger.Instance, fps);
                Assert.IsTrue(await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(3), timeout.Token));
            }

            await WaitForPopupPixelsAsync(expectedVisible: false);
            fx.OnUiThread(() =>
            {
                var origin = fx.Form.PointToScreen(System.Drawing.Point.Empty);
                popup = new PopupWindow
                {
                    Bounds = new System.Drawing.Rectangle(origin.X + 30, origin.Y + 50, 100, 90),
                };
                popup.Show(fx.Form);
                popup.Refresh();
            });

            await WaitForPopupPixelsAsync(expectedVisible: continuous);
            fx.OnUiThread(() => popup!.Close());
            await WaitForPopupPixelsAsync(expectedVisible: false);
            fx.OnUiThread(() =>
            {
                popup!.Dispose();
                var origin = fx.Form.PointToScreen(System.Drawing.Point.Empty);
                popup = new PopupWindow
                {
                    Bounds = new System.Drawing.Rectangle(origin.X + 30, origin.Y + 50, 100, 90),
                };
                popup.Show(fx.Form);
                popup.Refresh();
            });
            await WaitForPopupPixelsAsync(expectedVisible: continuous);
            fx.OnUiThread(() => popup!.Close());
            await WaitForPopupPixelsAsync(expectedVisible: false);
        }
        finally
        {
            grabber?.Dispose();
            fx.OnUiThread(() => popup?.Dispose());
        }

        async Task WaitForPopupPixelsAsync(bool expectedVisible)
        {
            var deadline = Environment.TickCount64 + 3000;
            long version = -1;
            var greenPixels = 0;
            var redPixels = 0;
            do
            {
                timeout.Token.ThrowIfCancellationRequested();
                byte[] pixels;
                if (grabber is not null)
                {
                    var latest = grabber.TryGetLatest();
                    if (latest is null)
                    {
                        await Task.Delay(50, timeout.Token);
                        continue;
                    }
                    pixels = latest.Value.Pixels;
                    version = latest.Value.Version;
                }
                else
                {
                    pixels = (await WgcCapture.CaptureAsync(new HWND(fx.Hwnd), NullLogger.Instance, timeout.Token)).Pixels;
                }
                greenPixels = 0;
                redPixels = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] == 0 && pixels[i + 1] == 255 && pixels[i + 2] == 0)
                    {
                        greenPixels++;
                    }
                    if (pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 255)
                    {
                        redPixels++;
                    }
                }
                if (redPixels >= 1000 &&
                    (expectedVisible ? greenPixels >= 1000 : greenPixels == 0) &&
                    (grabber is null || version > lastMatchedVersion))
                {
                    lastMatchedVersion = version;
                    return;
                }
                await Task.Delay(50, timeout.Token);
            }
            while (Environment.TickCount64 < deadline);

            Assert.Fail($"Popup pixels did not become {(expectedVisible ? "visible" : "absent")} in {(continuous ? "recording" : "screenshot")} capture (fps={fps}, version={version}, previous={lastMatchedVersion}, green={greenPixels}, red={redPixels}).");
        }
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task Recording_PopupsOpeningAndClosingDoNotLeakCaptureResources()
    {
        if (!WgcCapture.IsSupported())
        {
            Assert.Inconclusive("Owned-popup capture requires WGC.");
        }
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Popup rendering requires an unlocked interactive desktop.");
        }

        using var fx = new UiaTestFixture();
        using var grabber = WgcCapture.StartGrabber(new HWND(fx.Hwnd), NullLogger.Instance, 30);
        Assert.IsTrue(await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(3), CancellationToken.None));

        async Task CycleAsync()
        {
            PopupWindow? popup = null;
            fx.OnUiThread(() =>
            {
                var origin = fx.Form.PointToScreen(System.Drawing.Point.Empty);
                popup = new PopupWindow { Bounds = new System.Drawing.Rectangle(origin.X + 30, origin.Y + 50, 100, 90) };
                popup.Show(fx.Form);
                popup.Refresh();
            });
            await SampleAsync(150);
            fx.OnUiThread(() => popup!.Dispose());
            await SampleAsync(60);
        }

        async Task SampleAsync(int milliseconds)
        {
            var end = Environment.TickCount64 + milliseconds;
            while (Environment.TickCount64 < end)
            {
                grabber.TryGetLatest();
                await Task.Delay(30);
            }
        }

        // Warm up so one-time allocations are not counted.
        for (var i = 0; i < 3; i++)
        {
            await CycleAsync();
        }
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var before = process.HandleCount;
        const int cycles = 30;
        for (var i = 0; i < cycles; i++)
        {
            await CycleAsync();
        }
        // Released capture objects are reclaimed by a collection; leaked references never are.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        var growth = process.HandleCount - before;
        TestContext.WriteLine($"Capture handle growth: {growth} over {cycles} popup cycles (before={before}, after={process.HandleCount}).");

        // Before the fix each cycle leaked a D3D device (about 26 handles); allow for thread-pool noise.
        Assert.IsLessThan(cycles * 5, growth, $"Handle count grew by {growth} over {cycles} popup open/close cycles.");
    }

    private sealed class PopupWindow : Form
    {
        internal void SetPerPixelOpacity()
        {
            const int WS_EX_LAYERED = 0x00080000;
            _ = SetWindowLongW(Handle, -20, GetWindowLongW(Handle, -20) | WS_EX_LAYERED);
            using var bitmap = new System.Drawing.Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.FromArgb(128, 0, 255, 0));
            }
            var bitmapHandle = bitmap.GetHbitmap(System.Drawing.Color.FromArgb(0));
            var dc = CreateCompatibleDC(0);
            nint previous = 0;
            try
            {
                Assert.AreNotEqual(nint.Zero, dc);
                previous = SelectObject(dc, bitmapHandle);
                Assert.AreNotEqual(nint.Zero, previous);
                var destination = new NativePoint { X = Left, Y = Top };
                var origin = new NativePoint();
                var size = new NativePoint { X = Width, Y = Height };
                var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
                Assert.IsTrue(UpdateLayeredWindow(Handle, 0, ref destination, ref size, dc, ref origin, 0, ref blend, 2),
                    $"UpdateLayeredWindow failed: {Marshal.GetLastPInvokeError()}");
            }
            finally
            {
                if (previous != 0)
                {
                    SelectObject(dc, previous);
                }
                DeleteObject(bitmapHandle);
                if (dc != 0)
                {
                    DeleteDC(dc);
                }
            }
        }

        public PopupWindow()
        {
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = System.Drawing.Color.Lime;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Style = unchecked((int)0x80000000); // WS_POPUP
                parameters.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return parameters;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(nint hwnd, int index, int value);
    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(nint hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(nint hwnd, nint screenDc, ref NativePoint destination,
        ref NativePoint size, nint sourceDc, ref NativePoint source, uint key, ref BlendFunction blend, uint flags);
    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);
}
