// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class UiCaptureOptionsTests
{
    [TestMethod]
    public void ContrastAudit_BoundsPixelsAndDisablesPrintWindowFallback()
    {
        var options = UiWindowCaptureOptions.ContrastAudit;

        Assert.AreEqual(UiWindowCaptureOptions.AuditMaxPixelCount, options.MaxPixelCount);
        Assert.IsFalse(options.AllowPrintWindowFallback);
        Assert.IsTrue(UiWindowCaptureOptions.Screenshot.AllowPrintWindowFallback);
        Assert.IsNull(UiWindowCaptureOptions.Screenshot.MaxPixelCount);
        Assert.AreEqual(TimeSpan.FromSeconds(10), UiAuditCommand.AuditMaxContrastDuration);
    }

    [TestMethod]
    public void CaptureBounds_AcceptsLimitAndRejectsOnePixelOver()
    {
        var atLimit = UiCaptureBounds.GetRequiredByteLength(
            4096,
            4096,
            UiWindowCaptureOptions.AuditMaxPixelCount,
            "WGC frame");

        Assert.AreEqual(67_108_864, atLimit);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            UiCaptureBounds.GetRequiredByteLength(
                4096,
                4097,
                UiWindowCaptureOptions.AuditMaxPixelCount,
                "WGC frame"));
        Assert.AreEqual(
            "WGC frame is 4096x4097 (16781312 pixels), exceeding the 16777216-pixel accessibility audit capture limit.",
            ex.Message);
    }

    [TestMethod]
    public void CaptureBounds_RejectsInvalidDimensionsBeforeAllocation()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            UiCaptureBounds.GetRequiredByteLength(
                0,
                100,
                UiWindowCaptureOptions.AuditMaxPixelCount,
                "Window capture"));

        Assert.AreEqual("Window capture has invalid dimensions 0x100.", ex.Message);
    }

    [TestMethod]
    public void CaptureBounds_RejectsUnrepresentableBufferWithoutArithmeticOverflow()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            UiCaptureBounds.GetRequiredByteLength(
                int.MaxValue,
                int.MaxValue,
                maxPixelCount: null,
                "Window capture"));

        StringAssert.Contains(ex.Message, "exceeding the maximum supported capture buffer");
    }
}
