// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal static class OwnedPopupComposition
{
    // WGC surfaces use premultiplied BGRA; keep the result premultiplied, including alpha.
    internal static void Blend(byte[] root, int width, int height, byte[] popup,
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
}
