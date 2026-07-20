// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Computes WCAG 2.x contrast ratios from captured pixels. Pure functions only — no UIA or
/// capture dependencies — so the logic is unit-testable with synthetic pixel buffers.
/// </summary>
internal static class ContrastAnalyzer
{
    /// <summary>
    /// A rectangle in buffer (pixel) coordinates.
    /// </summary>
    internal readonly record struct PixelRect(int X, int Y, int Width, int Height);

    /// <summary>Pixels with an alpha below this are treated as transparent and ignored.</summary>
    private const byte AlphaOpaqueThreshold = 250;

    /// <summary>Maximum sampled pixels for one candidate rectangle.</summary>
    internal const int MaxSamplePixels = 65_536;

    private const int LuminanceBinCount = 4096;

    /// <summary>
    /// A rect is "not measured" (returns null) unless at least this fraction of its pixels are
    /// opaque. Guards transparent/layered regions that would otherwise be scored as raw (often
    /// black) RGB.
    /// </summary>
    private const double MinOpaqueFraction = 0.40;

    /// <summary>Foreground (glyph) cluster must contain at least this many opaque pixels.</summary>
    private const int MinForegroundPixels = 8;

    /// <summary>Foreground (glyph) cluster must be at least this fraction of opaque pixels.</summary>
    private const double MinForegroundFraction = 0.005;

    /// <summary>
    /// Below this luminance spread the region is treated as effectively uniform (no measurable
    /// text), so no contrast is reported. Prevents solid fills collapsing to a fabricated ~1:1.
    /// </summary>
    private const double UniformLuminanceEpsilon = 1e-4;

    /// <summary>
    /// Estimate the WCAG contrast ratio between foreground (text) and background luminance
    /// within a region of a BGRA (32-bpp, top-down) pixel buffer.
    /// <para>
    /// Text glyphs are typically a minority of pixels within a text element's bounding box. This
    /// separates a background cluster from a foreground cluster (1D Otsu split over opaque-pixel
    /// luminance) and reports the ratio between their representative luminances. Alpha is honored:
    /// transparent/layered pixels are ignored, and a mostly-transparent rect is "not measured".
    /// </para>
    /// Returns <c>null</c> when the region cannot be meaningfully measured — degenerate/out-of-buffer,
    /// mostly transparent, effectively uniform (no glyphs), or the glyph cluster is too small to
    /// trust (sparse-glyph guard). This deliberately avoids fabricating a low ratio for sparse or
    /// transparent content.
    /// </summary>
    public static double? ComputeContrastRatio(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        PixelRect rect)
        => ComputeContrastRatio(bgra, width, height, rect, MaxSamplePixels, CancellationToken.None);

    public static double? ComputeContrastRatio(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        PixelRect rect,
        int maxSamplePixels,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSamplePixels, 1);

        if (width <= 0 || height <= 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }
        if ((long)width * height > bgra.Length / 4L)
        {
            return null;
        }

        // Clamp the region to the buffer bounds.
        var x0 = Math.Max(0, rect.X);
        var y0 = Math.Max(0, rect.Y);
        var x1 = (int)Math.Min(width, (long)rect.X + rect.Width);
        var y1 = (int)Math.Min(height, (long)rect.Y + rect.Height);
        if (x1 <= x0 || y1 <= y0)
        {
            return null;
        }

        var regionWidth = x1 - x0;
        var regionHeight = y1 - y0;
        var (sampleWidth, sampleHeight) = GetSampleGridSize(
            regionWidth,
            regionHeight,
            maxSamplePixels);
        var totalSamples = sampleWidth * sampleHeight;

        // Accumulate exact luminance sums in a fixed-size histogram. The stratified grid bounds
        // CPU while preserving deterministic coverage across the full rectangle; the histogram
        // bounds memory and avoids sorting one value per pixel.
        Span<int> counts = stackalloc int[LuminanceBinCount];
        Span<double> sums = stackalloc double[LuminanceBinCount];
        counts.Clear();
        sums.Clear();
        var opaqueCount = 0;
        var totalLuminance = 0.0;
        var minLuminance = double.MaxValue;
        var maxLuminance = double.MinValue;

        for (var sampleY = 0; sampleY < sampleHeight; sampleY++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var y = y0 + (int)(((2L * sampleY + 1) * regionHeight) / (2L * sampleHeight));
            var rowOffset = y * width * 4;
            for (var sampleX = 0; sampleX < sampleWidth; sampleX++)
            {
                var x = x0 + (int)(((2L * sampleX + 1) * regionWidth) / (2L * sampleWidth));
                var p = rowOffset + x * 4;
                // BGRA order.
                var b = bgra[p];
                var g = bgra[p + 1];
                var r = bgra[p + 2];
                var a = bgra[p + 3];
                if (a < AlphaOpaqueThreshold)
                {
                    continue;
                }

                var luminance = RelativeLuminance(r, g, b);
                var bin = Math.Min(
                    LuminanceBinCount - 1,
                    (int)(luminance * (LuminanceBinCount - 1)));
                counts[bin]++;
                sums[bin] += luminance;
                opaqueCount++;
                totalLuminance += luminance;
                minLuminance = Math.Min(minLuminance, luminance);
                maxLuminance = Math.Max(maxLuminance, luminance);
            }
        }

        // Mostly transparent (or fully) → not measured.
        if (opaqueCount == 0 || (double)opaqueCount / totalSamples < MinOpaqueFraction)
        {
            return null;
        }

        // Effectively uniform region (a solid fill, not text) → not measured.
        if (maxLuminance - minLuminance < UniformLuminanceEpsilon)
        {
            return null;
        }

        // Separate a background cluster from a foreground (glyph) cluster via a 1D Otsu split that
        // maximizes between-class variance, then require the minority (glyph) cluster to clear a
        // small coverage floor. This stops short text from collapsing to a fabricated ~1:1.
        var split = OtsuSplit(counts, sums, opaqueCount, totalLuminance);
        if (split is not { } clusters)
        {
            return null;
        }

        var (lowMean, lowCount, highMean, highCount) = clusters;
        var minorityCount = Math.Min(lowCount, highCount);
        var floor = Math.Max(MinForegroundPixels, (int)Math.Ceiling(opaqueCount * MinForegroundFraction));
        if (minorityCount < floor)
        {
            return null;
        }

        return ContrastRatio(lowMean, highMean);
    }

    /// <summary>
    /// Return the deterministic sample-grid dimensions for a region. The product never exceeds
    /// <paramref name="maxSamplePixels"/> and tracks the source aspect ratio.
    /// </summary>
    internal static (int Width, int Height) GetSampleGridSize(
        int width,
        int height,
        int maxSamplePixels = MaxSamplePixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSamplePixels, 1);
        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        if ((long)width * height <= maxSamplePixels)
        {
            return (width, height);
        }

        var idealWidth = (int)Math.Round(
            Math.Sqrt(maxSamplePixels * (double)width / height));
        var sampleWidth = Math.Clamp(idealWidth, 1, Math.Min(width, maxSamplePixels));
        var sampleHeight = Math.Clamp(maxSamplePixels / sampleWidth, 1, height);
        return (sampleWidth, sampleHeight);
    }

    /// <summary>
    /// 1D two-class split (Otsu) over fixed luminance bins. Returns exact means from the sampled
    /// luminance sums and counts using the split that maximizes between-class variance.
    /// </summary>
    private static (double LowMean, int LowCount, double HighMean, int HighCount)? OtsuSplit(
        ReadOnlySpan<int> counts,
        ReadOnlySpan<double> sums,
        int totalCount,
        double totalSum)
    {
        var sumLow = 0.0;
        var countLow = 0;
        var bestBetween = -1.0;
        var bestLowSum = 0.0;
        var bestLowCount = 0;

        for (var i = 0; i < counts.Length - 1; i++)
        {
            countLow += counts[i];
            sumLow += sums[i];
            var countHigh = totalCount - countLow;
            if (countLow == 0 || countHigh == 0)
            {
                continue;
            }

            var meanLow = sumLow / countLow;
            var meanHigh = (totalSum - sumLow) / countHigh;
            var diff = meanLow - meanHigh;
            var between = (double)countLow * countHigh * diff * diff;
            if (between > bestBetween)
            {
                bestBetween = between;
                bestLowSum = sumLow;
                bestLowCount = countLow;
            }
        }

        if (bestLowCount == 0 || bestLowCount == totalCount)
        {
            return null;
        }

        var highCount = totalCount - bestLowCount;
        return (
            bestLowSum / bestLowCount,
            bestLowCount,
            (totalSum - bestLowSum) / highCount,
            highCount);
    }

    /// <summary>
    /// WCAG contrast ratio between two relative luminances (order-independent).
    /// </summary>
    public static double ContrastRatio(double l1, double l2)
    {
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// WCAG relative luminance for an sRGB color (channels 0-255).
    /// </summary>
    public static double RelativeLuminance(byte r, byte g, byte b)
    {
        var rl = Linearize(r / 255.0);
        var gl = Linearize(g / 255.0);
        var bl = Linearize(b / 255.0);
        return 0.2126 * rl + 0.7152 * gl + 0.0722 * bl;
    }

    private static double Linearize(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
}
