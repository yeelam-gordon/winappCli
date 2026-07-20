// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

internal readonly record struct UiWindowCaptureOptions(
    long? MaxPixelCount,
    bool AllowPrintWindowFallback)
{
    public const long AuditMaxPixelCount = 16_777_216;

    public static UiWindowCaptureOptions Screenshot { get; } = new(
        MaxPixelCount: null,
        AllowPrintWindowFallback: true);

    public static UiWindowCaptureOptions ContrastAudit { get; } = new(
        MaxPixelCount: AuditMaxPixelCount,
        AllowPrintWindowFallback: false);
}

internal static class UiCaptureBounds
{
    public static int GetRequiredByteLength(
        int width,
        int height,
        long? maxPixelCount,
        string source)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"{source} has invalid dimensions {width}x{height}.");
        }

        if (maxPixelCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPixelCount),
                maxPixelCount,
                "The capture pixel limit must be positive.");
        }

        var pixelCount = checked((long)width * height);
        if (maxPixelCount is { } limit && pixelCount > limit)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"{source} is {width}x{height} ({pixelCount} pixels), exceeding the {limit}-pixel accessibility audit capture limit."));
        }

        var maxBufferPixels = Array.MaxLength / 4L;
        if (pixelCount > maxBufferPixels)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"{source} has {pixelCount} pixels, exceeding the maximum supported capture buffer."));
        }

        return checked((int)(pixelCount * 4));
    }
}
