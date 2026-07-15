// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.UI.Input.Pointer;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class PointerInputCleanupTests
{
    [TestMethod]
    public void InjectTouchStroke_InitialDownFails_CancelsEveryPossibleContact()
    {
        var downFailure = new InvalidOperationException("initial touch DOWN failed");
        POINTER_TOUCH_INFO[]? cleanupFrame = null;
        int callCount = 0;
        PointerInput.TouchSender sender = contacts =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw downFailure;
            }

            cleanupFrame = contacts.ToArray();
        };
        var paths = new List<IReadOnlyList<PointerPoint>>
        {
            new List<PointerPoint> { new(-40, 25) },
            new List<PointerPoint> { new(60, -75) },
        };

        var caught = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PointerInput.InjectTouchStroke(paths, holdMs: 0, durationMs: 0, sender));

        Assert.AreSame(downFailure, caught);
        Assert.AreEqual(2, callCount, "Initial failure must make exactly one bounded cleanup attempt");
        Assert.IsNotNull(cleanupFrame);
        Assert.AreEqual(2, cleanupFrame.Length);
        Assert.AreEqual(-40, cleanupFrame[0].pointerInfo.ptPixelLocation.X);
        Assert.AreEqual(25, cleanupFrame[0].pointerInfo.ptPixelLocation.Y);
        Assert.AreEqual(60, cleanupFrame[1].pointerInfo.ptPixelLocation.X);
        Assert.AreEqual(-75, cleanupFrame[1].pointerInfo.ptPixelLocation.Y);
        Assert.IsTrue(cleanupFrame.All(contact =>
            contact.pointerInfo.pointerFlags.HasFlag(POINTER_FLAGS.POINTER_FLAG_UP) &&
            contact.pointerInfo.pointerFlags.HasFlag(POINTER_FLAGS.POINTER_FLAG_CANCELED)));
    }

    [TestMethod]
    public void InjectTouchStroke_InitialDownAndCleanupFail_ReportsBothExactly()
    {
        var downFailure = new InvalidOperationException("initial touch DOWN failed");
        var cleanupFailure = new InvalidOperationException("touch cancellation failed");
        int callCount = 0;
        PointerInput.TouchSender sender = _ =>
        {
            throw ++callCount == 1 ? downFailure : cleanupFailure;
        };
        var paths = new List<IReadOnlyList<PointerPoint>>
        {
            new List<PointerPoint> { new(100, 200) },
        };

        var caught = Assert.ThrowsExactly<PointerInjectionException>(() =>
            PointerInput.InjectTouchStroke(paths, holdMs: 0, durationMs: 0, sender));

        Assert.AreEqual(2, callCount);
        Assert.AreSame(downFailure, caught.PrimaryFailure);
        Assert.AreSame(cleanupFailure, caught.CancellationFailure);
        Assert.AreEqual(
            "initial touch DOWN failed Best-effort canceled-UP cleanup also failed: touch cancellation failed",
            caught.Message);
    }

    [TestMethod]
    public void InjectPenStroke_InitialDownFails_SendsCanceledUpAtInitialPoint()
    {
        var downFailure = new InvalidOperationException("initial pen DOWN failed");
        (int X, int Y, uint Pressure, POINTER_FLAGS Flags)? cleanup = null;
        int callCount = 0;
        PointerInput.PenFrameSender sender = (x, y, pressure, flags) =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw downFailure;
            }

            cleanup = (x, y, pressure, flags);
        };

        var caught = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PointerInput.InjectPenStroke(
                [new PointerPoint(-300, 450)],
                contactPressure: 512,
                durationMs: 0,
                sender));

        Assert.AreSame(downFailure, caught);
        Assert.AreEqual(2, callCount);
        Assert.IsNotNull(cleanup);
        Assert.AreEqual(-300, cleanup.Value.X);
        Assert.AreEqual(450, cleanup.Value.Y);
        Assert.AreEqual(0u, cleanup.Value.Pressure);
        Assert.IsTrue(cleanup.Value.Flags.HasFlag(POINTER_FLAGS.POINTER_FLAG_UP));
        Assert.IsTrue(cleanup.Value.Flags.HasFlag(POINTER_FLAGS.POINTER_FLAG_CANCELED));
    }

    [TestMethod]
    public void InjectPenStroke_InitialDownAndCleanupFail_ReportsBothExactly()
    {
        var downFailure = new InvalidOperationException("initial pen DOWN failed");
        var cleanupFailure = new InvalidOperationException("pen cancellation failed");
        int callCount = 0;
        PointerInput.PenFrameSender sender = (_, _, _, _) =>
        {
            throw ++callCount == 1 ? downFailure : cleanupFailure;
        };

        var caught = Assert.ThrowsExactly<PointerInjectionException>(() =>
            PointerInput.InjectPenStroke(
                [new PointerPoint(10, 20)],
                contactPressure: 512,
                durationMs: 0,
                sender));

        Assert.AreEqual(2, callCount);
        Assert.AreSame(downFailure, caught.PrimaryFailure);
        Assert.AreSame(cleanupFailure, caught.CancellationFailure);
        Assert.AreEqual(
            "initial pen DOWN failed Best-effort canceled-UP cleanup also failed: pen cancellation failed",
            caught.Message);
    }
}
