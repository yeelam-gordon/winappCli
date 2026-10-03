// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class WgcSecondaryWindowTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Capture_IncludesOpenPopupAndRemovesItWhenClosed(bool continuous)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) || !WgcCapture.IsSupported())
        {
            Assert.Inconclusive("Secondary-window capture requires Windows 11 24H2 and WGC.");
        }
        if (ForegroundGuard.NoInteractiveDesktop())
        {
            Assert.Inconclusive("Popup rendering requires an unlocked interactive desktop.");
        }

        using var fx = new UiaTestFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        PopupWindow? popup = null;
        WgcCapture.FrameGrabber? grabber = null;
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
                grabber = WgcCapture.StartGrabber(new HWND(fx.Hwnd), NullLogger.Instance, 15);
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

            await WaitForPopupPixelsAsync(expectedVisible: true);
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
            do
            {
                timeout.Token.ThrowIfCancellationRequested();
                byte[] pixels;
                if (grabber is not null)
                {
                    pixels = grabber.TryGetLatest()!.Value.Pixels;
                }
                else
                {
                    pixels = (await WgcCapture.CaptureAsync(new HWND(fx.Hwnd), NullLogger.Instance, timeout.Token)).Pixels;
                }
                var greenPixels = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] == 0 && pixels[i + 1] == 255 && pixels[i + 2] == 0)
                    {
                        greenPixels++;
                    }
                }
                if (expectedVisible ? greenPixels >= 1000 : greenPixels == 0)
                {
                    return;
                }
                await Task.Delay(50, timeout.Token);
            }
            while (Environment.TickCount64 < deadline);

            Assert.Fail($"Popup pixels did not become {(expectedVisible ? "visible" : "absent")} in {(continuous ? "recording" : "screenshot")} capture.");
        }
    }

    [TestMethod]
    public void SupportedSession_EnablesSecondaryWindowsAndReleasesInterface()
    {
        using var session = new NativeSession();

        Assert.IsTrue(WgcCapture.TryIncludeSecondaryWindows(session.Pointer));
        Assert.AreEqual(1, session.SetterCalls);
        Assert.AreEqual((byte)1, session.Value);
        Assert.AreEqual(1, session.References);
    }

    [TestMethod]
    public void OlderWindows_KeepsWindowCaptureWithoutCallingMissingSetter()
    {
        using var session = new NativeSession { QueryResult = unchecked((int)0x80004002) };

        Assert.IsFalse(WgcCapture.TryIncludeSecondaryWindows(session.Pointer));
        Assert.AreEqual(0, session.SetterCalls);
        Assert.AreEqual(1, session.References);
    }

    [TestMethod]
    public void QueryFailure_IsNotTreatedAsUnsupportedWindows()
    {
        using var session = new NativeSession { QueryResult = unchecked((int)0x80004005) };

        var exception = Assert.ThrowsExactly<COMException>(() =>
            WgcCapture.TryIncludeSecondaryWindows(session.Pointer));
        Assert.AreEqual(session.QueryResult, exception.HResult);
        Assert.AreEqual(0, session.SetterCalls);
        Assert.AreEqual(1, session.References);
    }

    [TestMethod]
    public void SetterFailure_IsReportedAndReleasesInterface()
    {
        using var session = new NativeSession { SetterResult = unchecked((int)0x80004005) };

        var exception = Assert.ThrowsExactly<COMException>(() =>
            WgcCapture.TryIncludeSecondaryWindows(session.Pointer));
        Assert.AreEqual(session.SetterResult, exception.HResult);
        Assert.AreEqual(1, session.SetterCalls);
        Assert.AreEqual(1, session.References);
    }

    private sealed class PopupWindow : Form
    {
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

    private sealed unsafe class NativeSession : IDisposable
    {
        private readonly State* _state;
        private readonly nint* _vtable;

        public NativeSession()
        {
            _vtable = (nint*)NativeMemory.AllocZeroed(8, (nuint)sizeof(nint));
            _vtable[0] = (nint)(delegate* unmanaged[Stdcall]<State*, Guid*, nint*, int>)&QueryInterface;
            _vtable[1] = (nint)(delegate* unmanaged[Stdcall]<State*, uint>)&AddRef;
            _vtable[2] = (nint)(delegate* unmanaged[Stdcall]<State*, uint>)&Release;
            _vtable[7] = (nint)(delegate* unmanaged[Stdcall]<State*, byte, int>)&SetValue;
            _state = (State*)NativeMemory.AllocZeroed((nuint)sizeof(State));
            _state->Vtable = _vtable;
            _state->References = 1;
        }

        public nint Pointer => (nint)_state;
        public int References => _state->References;
        public int SetterCalls => _state->SetterCalls;
        public byte Value => _state->Value;
        public int QueryResult { get => _state->QueryResult; init => _state->QueryResult = value; }
        public int SetterResult { get => _state->SetterResult; init => _state->SetterResult = value; }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(State* state, Guid* iid, nint* result)
        {
            *result = 0;
            if (*iid != new Guid("D7419236-BE20-5E9F-BCD6-C4E98FD6AFDC"))
            {
                return unchecked((int)0x80004002);
            }
            if (state->QueryResult < 0)
            {
                return state->QueryResult;
            }
            state->References++;
            *result = (nint)state;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(State* state) => (uint)++state->References;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(State* state) => (uint)--state->References;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetValue(State* state, byte value)
        {
            state->SetterCalls++;
            state->Value = value;
            return state->SetterResult;
        }

        public void Dispose()
        {
            NativeMemory.Free(_state);
            NativeMemory.Free(_vtable);
        }

        private struct State
        {
            public nint* Vtable;
            public int References;
            public int QueryResult;
            public int SetterResult;
            public int SetterCalls;
            public byte Value;
        }
    }
}
