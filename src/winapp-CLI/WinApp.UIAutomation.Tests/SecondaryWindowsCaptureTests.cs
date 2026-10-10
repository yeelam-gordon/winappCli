// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.Foundation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class SecondaryWindowsCaptureTests
{
    [TestMethod]
    public void Blend_PreservesPremultipliedAlpha()
    {
        byte[] root = [0, 0, 128, 128];
        SecondaryWindowsCapture.IncludeSecondaryWindowInWindowImage(root, 1, 1, [0, 128, 0, 128], 1, 1, 0, 0);
        CollectionAssert.AreEqual(new byte[] { 0, 128, 64, 192 }, root);
        var before = (byte[])root.Clone();
        SecondaryWindowsCapture.IncludeSecondaryWindowInWindowImage(root, 1, 1, [0, 0, 0, 0], 1, 1, 0, 0);
        CollectionAssert.AreEqual(before, root);
    }

    [TestMethod]
    public void Blend_ClipsNegativeOriginAndOutsideRoot()
    {
        var root = new byte[16];
        byte[] popup = [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];
        SecondaryWindowsCapture.IncludeSecondaryWindowInWindowImage(root, 2, 2, popup, 2, 2, -1, -1);
        CollectionAssert.AreEqual(new byte[] { 10, 11, 12, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, root);
        var before = (byte[])root.Clone();
        SecondaryWindowsCapture.IncludeSecondaryWindowInWindowImage(root, 2, 2, popup, 2, 2, int.MaxValue, int.MinValue);
        CollectionAssert.AreEqual(before, root);
    }

    [TestMethod]
    public void Ownership_ExcludesUnrelatedForeignAndCyclicOwners()
    {
        try
        {
            RealOwnedWindowFinder.s_getWindowProcessId = hwnd => (nint)hwnd == 9 ? 2 : 1;
            RealOwnedWindowFinder.s_getWindowOwner = hwnd => new HWND((nint)hwnd switch
            {
                3 => 2, 2 => 1, 4 => 0, 5 => 6, 6 => 5, 9 => 1, _ => 0
            });
            Assert.IsTrue(SecondaryWindowsCapture.IsOwnedBy(new HWND(3), new HWND(1), 1));
            Assert.IsFalse(SecondaryWindowsCapture.IsOwnedBy(new HWND(4), new HWND(1), 1));
            Assert.IsFalse(SecondaryWindowsCapture.IsOwnedBy(new HWND(5), new HWND(1), 1));
            Assert.IsFalse(SecondaryWindowsCapture.IsOwnedBy(new HWND(9), new HWND(1), 1));
        }
        finally
        {
            RealOwnedWindowFinder.ResetNativeSeams();
        }
    }

    [TestMethod]
    public void Sampling_CapturesAllSeventeenSecondaryWindowsAndReusesSessions()
    {
        var root = new FakeGrabber([0, 0, 255, 255]);
        var windows = Enumerable.Range(2, 17)
            .Select(handle => new SecondaryWindowsCapture.Popup(handle, new(0, 0, 1, 1))).ToList();
        var captures = new List<FakeGrabber>();
        using var combined = new SecondaryWindowsCapture(root, () => new(0, 0, 1, 1),
            () => windows.ToList(), _ =>
            {
                var capture = new FakeGrabber([0, 255, 0, 255]);
                captures.Add(capture);
                return capture;
            });

        var first = combined.TryGetLatest()!.Value;
        Assert.AreEqual(17, captures.Count);
        CollectionAssert.AreEqual(new byte[] { 0, 255, 0, 255 }, first.Pixels);
        Assert.AreSame(first.Pixels, combined.TryGetLatest()!.Value.Pixels);
        Assert.AreEqual(17, captures.Count);

        windows.Clear();
        Assert.AreSame(root.Pixels, combined.TryGetLatest()!.Value.Pixels);
        Assert.IsTrue(captures.All(capture => capture.Disposed));
    }

    [TestMethod]
    public void Sampling_AddsRemovesAndReopensPopupWithoutMutatingRoot()
    {
        var root = new FakeGrabber([0, 0, 255, 255]);
        var started = new List<FakeGrabber>();
        var popups = new List<SecondaryWindowsCapture.Popup>();
        using var composite = new SecondaryWindowsCapture(root, () => new(0, 0, 1, 1),
            () => popups.ToList(), _ =>
            {
                var child = new FakeGrabber([0, 255, 0, 255]);
                started.Add(child);
                return child;
            });
        CollectionAssert.AreEqual(root.Pixels, composite.TryGetLatest()!.Value.Pixels);
        popups.Add(new(2, new(0, 0, 1, 1)));
        CollectionAssert.AreEqual(new byte[] { 0, 255, 0, 255 }, composite.TryGetLatest()!.Value.Pixels);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255 }, root.Pixels);
        popups.Clear();
        CollectionAssert.AreEqual(root.Pixels, composite.TryGetLatest()!.Value.Pixels);
        Assert.IsTrue(started[0].Disposed);
        popups.Add(new(2, new(0, 0, 1, 1)));
        composite.TryGetLatest();
        Assert.AreEqual(2, started.Count);
        root.IsClosed = true;
        composite.TryGetLatest();
        Assert.IsTrue(started[1].Disposed);
    }

    [TestMethod]
    public void Sampling_PaintsTopmostPopupLastAndDisposesEverySession()
    {
        var root = new FakeGrabber([0, 0, 255, 255]);
        var top = new FakeGrabber([128, 0, 0, 128]);
        var bottom = new FakeGrabber([0, 255, 0, 255]);
        var composite = new SecondaryWindowsCapture(root, () => new(0, 0, 1, 1),
            () => [new(2, new(0, 0, 1, 1)), new(3, new(0, 0, 1, 1))],
            handle => handle == 2 ? top : bottom);
        try
        {
            CollectionAssert.AreEqual(new byte[] { 128, 127, 0, 255 },
                composite.TryGetLatest()!.Value.Pixels);
        }
        finally
        {
            composite.Dispose();
        }
        Assert.IsTrue(root.Disposed);
        Assert.IsTrue(top.Disposed);
        Assert.IsTrue(bottom.Disposed);
    }

    [TestMethod]
    public void Sampling_UnchangedInputsRetainVersionAndPixelReference()
    {
        var root = new FakeGrabber([0, 0, 255, 255]);
        using var empty = new SecondaryWindowsCapture(root, () => new(0, 0, 1, 1),
            () => [], _ => throw new AssertFailedException("No popup session should be started."));
        var first = empty.TryGetLatest()!.Value;
        var repeated = empty.TryGetLatest()!.Value;
        Assert.AreSame(root.Pixels, first.Pixels);
        Assert.AreSame(first.Pixels, repeated.Pixels);
        Assert.AreEqual(first.Version, repeated.Version);

        var child = new FakeGrabber([0, 255, 0, 255]);
        using var composed = new SecondaryWindowsCapture(root, () => new(0, 0, 1, 1),
            () => [new(2, new(0, 0, 1, 1))], _ => child);
        first = composed.TryGetLatest()!.Value;
        repeated = composed.TryGetLatest()!.Value;
        Assert.AreNotSame(root.Pixels, first.Pixels);
        Assert.AreSame(first.Pixels, repeated.Pixels);
        Assert.AreEqual(first.Version, repeated.Version);
    }

    [TestMethod]
    public void Sampling_ChangedCaptureBoundsOrderAndMembershipAdvanceVersion()
    {
        var root = new FakeGrabber([0, 0, 255, 255]);
        var child = new FakeGrabber([0, 255, 0, 255]);
        var bounds = new PointerRect(0, 0, 1, 1);
        var popups = new List<SecondaryWindowsCapture.Popup> { new(2, bounds) };
        using var composite = new SecondaryWindowsCapture(root, () => bounds,
            () => popups.ToList(), _ => child);
        var version = composite.TryGetLatest()!.Value.Version;

        root.Version++;
        AssertAdvanced("root frame");
        child.Version++;
        AssertAdvanced("popup frame");
        popups[0] = new(2, new(-1, 0, 1, 1));
        AssertAdvanced("popup movement");
        bounds = new(-1, 0, 0, 1);
        AssertAdvanced("root movement");
        popups.Add(new(3, bounds));
        AssertAdvanced("popup addition");
        popups.Reverse();
        AssertAdvanced("z-order");
        popups.RemoveAt(0);
        AssertAdvanced("popup removal");
        popups.Clear();
        AssertAdvanced("last popup removal");
        Assert.AreSame(root.Pixels, composite.TryGetLatest()!.Value.Pixels);

        void AssertAdvanced(string change)
        {
            var frame = composite.TryGetLatest()!.Value;
            Assert.AreEqual(version + 1, frame.Version, change);
            version = frame.Version;
            var repeated = composite.TryGetLatest()!.Value;
            Assert.AreEqual(version, repeated.Version, $"unchanged after {change}");
            Assert.AreSame(frame.Pixels, repeated.Pixels, $"cached after {change}");
        }
    }

    [TestMethod]
    public void Sampling_ClosedDrainKeepsMonotonicVersionAndReleasesChildren()
    {
        var root = new FakeGrabber([0, 0, 255, 255]);
        var child = new FakeGrabber([0, 255, 0, 255]);
        using var composite = new SecondaryWindowsCapture(root, () => new(0, 0, 1, 1),
            () => [new(2, new(0, 0, 1, 1))], _ => child);
        composite.TryGetLatest();
        child.Version++;
        var beforeClose = composite.TryGetLatest()!.Value;
        Assert.IsGreaterThan(root.Version, beforeClose.Version);
        root.IsClosed = true;
        var drained = composite.TryGetLatest()!.Value;
        Assert.IsTrue(child.Disposed);
        Assert.AreEqual(beforeClose.Version, drained.Version);
        Assert.AreSame(beforeClose.Pixels, drained.Pixels);
        root.Version++;
        var final = composite.TryGetLatest()!.Value;
        Assert.AreEqual(drained.Version + 1, final.Version);
        Assert.AreSame(root.Pixels, final.Pixels);
        Assert.AreEqual(final.Version, composite.TryGetLatest()!.Value.Version);
    }

    private sealed class FakeGrabber(byte[] pixels) : IFrameGrabber
    {
        internal byte[] Pixels => pixels;
        internal bool Disposed { get; private set; }
        internal long Version { get; set; } = 1;
        public bool IsClosed { get; set; }
        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest() => (pixels, 1, 1, Version);
        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public void Dispose() => Disposed = true;
    }
}
