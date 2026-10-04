// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed partial class UiAutomationService
{
    internal static byte[] RenderOwnedSecondaryWindowForCapture(HWND hwnd, int width, int height,
        Func<HWND, HDC, bool>? print = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The owned secondary window has empty capture bounds.");
        }
        return CaptureFromWindow(hwnd, width, height, strict: true, print);
    }

    private static unsafe void SeedPopupBitmap(HDC dc, HBITMAP bitmap, int width, int height, byte marker)
    {
        // Distinct initial colors distinguish unpainted pixels from legitimate black.
        var seed = new byte[checked(width * height * 4)];
        Array.Fill(seed, marker);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width, biHeight = -height,
                biPlanes = 1, biBitCount = 32
            }
        };
        fixed (byte* data = seed)
        {
            if (SetDIBits(dc, bitmap, 0, (uint)height, data, &info, 0) != height)
            {
                throw PopupCaptureFailure("SetDIBits");
            }
        }
    }

    private static bool PopupBitmapHasSeedPixels(byte[] pixels, byte marker, byte[]? firstPass)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] == marker && pixels[i + 1] == marker && pixels[i + 2] == marker &&
                (firstPass is null ||
                    (firstPass[i] == 0x5A && firstPass[i + 1] == 0x5A && firstPass[i + 2] == 0x5A)))
            {
                return true;
            }
        }
        return false;
    }

    private static Win32Exception PopupCaptureFailure(string operation)
        => new(Marshal.GetLastPInvokeError(), $"{operation} failed during owned secondary window-only capture.");

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static unsafe partial int SetDIBits(HDC dc, HBITMAP bitmap, uint start, uint lines,
        byte* pixels, BITMAPINFO* info, uint usage);
}
