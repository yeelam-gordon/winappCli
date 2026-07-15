// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.UI.Input.Pointer;
using Windows.Win32.UI.WindowsAndMessaging;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class PointerNativeApiTests
{
    private static readonly IReadOnlyList<IReadOnlyList<PointerPoint>> OneTouchPath =
    [
        new List<PointerPoint> { new(100, 200) },
    ];

    private static readonly IReadOnlyList<IReadOnlyList<PointerPoint>> TwoTouchPaths =
    [
        new List<PointerPoint> { new(-120, 80) },
        new List<PointerPoint> { new(240, -160) },
    ];

    private static readonly IReadOnlyList<IReadOnlyList<PointerPoint>> SwipePath =
    [
        new List<PointerPoint> { new(10, 20), new(110, 120) },
    ];

    [TestMethod]
    public void Touch_CreateEntryPointMissing_FallsBackToLegacy()
    {
        var native = new FakePointerNativeApi
        {
            CreateException = new EntryPointNotFoundException(),
        };

        PointerInput.Touch(TouchGesture.Tap, TwoTouchPaths, 0, 0, native);

        Assert.AreEqual(1, native.CreateCallCount);
        Assert.AreEqual(0, native.DestroyCallCount);
        Assert.AreEqual(1, native.InitializeLegacyCallCount);
        Assert.AreEqual(2, native.LegacyFrames.Count);
        Assert.IsTrue(native.LegacyFrames.All(frame => frame.Length == 2));
    }

    [TestMethod]
    public void Touch_ModernCapabilityProbeThrowsDll_FallsBackToLegacy()
    {
        var native = new FakePointerNativeApi
        {
            ModernAvailabilityException = new DllNotFoundException(),
        };

        PointerInput.Touch(TouchGesture.Tap, OneTouchPath, 0, 0, native);

        Assert.AreEqual(0, native.CreateCallCount);
        Assert.AreEqual(1, native.InitializeLegacyCallCount);
        Assert.AreEqual(2, native.LegacyFrames.Count);
    }

    [TestMethod]
    public void Touch_InjectDllMissingBeforeFirstFrame_FallsBackAndDestroysDevice()
    {
        var native = new FakePointerNativeApi();
        native.SyntheticExceptions.Enqueue(new DllNotFoundException());

        PointerInput.Touch(TouchGesture.Tap, OneTouchPath, 0, 0, native);

        Assert.AreEqual(1, native.SyntheticFrames.Count);
        Assert.AreEqual(1, native.DestroyCallCount);
        Assert.AreEqual(2, native.LegacyFrames.Count);
    }

    [TestMethod]
    public void Touch_ModernApiDisappearsAfterDown_CleansUpWithoutLegacyReplay()
    {
        var native = new FakePointerNativeApi();
        native.SyntheticExceptionsByCall[2] = new EntryPointNotFoundException();

        var ex = Assert.ThrowsExactly<PointerNativeApiUnavailableException>(() =>
            PointerInput.Touch(TouchGesture.Tap, OneTouchPath, 0, 0, native));

        Assert.AreEqual(
            "InjectSyntheticPointerInput is unavailable because the required user32.dll entry point could not be loaded.",
            ex.Message);
        Assert.AreEqual(3, native.SyntheticFrames.Count);
        Assert.IsTrue(native.SyntheticFrames[2][0].Anonymous.touchInfo.pointerInfo.pointerFlags
            .HasFlag(POINTER_FLAGS.POINTER_FLAG_CANCELED));
        Assert.AreEqual(0, native.LegacyFrames.Count);
        Assert.AreEqual(1, native.DestroyCallCount);
    }

    [TestMethod]
    public void Touch_ApiDisappearsAfterAmbiguousInitialRetry_AttemptsCleanupWithoutFallback()
    {
        var native = new FakePointerNativeApi();
        native.SyntheticResults.Enqueue(false);
        native.LastErrors.Enqueue(21);
        native.SyntheticExceptionsByCall[2] = new EntryPointNotFoundException();
        native.SyntheticExceptionsByCall[3] = new EntryPointNotFoundException();

        var ex = Assert.ThrowsExactly<PointerInjectionException>(() =>
            PointerInput.Touch(TouchGesture.Tap, OneTouchPath, 0, 0, native));

        Assert.AreEqual(3, native.SyntheticFrames.Count);
        Assert.AreEqual(0, native.LegacyFrames.Count);
        var primary = Assert.IsInstanceOfType<PointerNativeApiUnavailableException>(ex.PrimaryFailure);
        Assert.IsTrue(primary.PriorNativeCallMayHaveBeenAttempted);
        Assert.IsInstanceOfType<PointerNativeApiUnavailableException>(ex.CancellationFailure);
        Assert.IsTrue(native.SyntheticFrames[2][0].Anonymous.touchInfo.pointerInfo.pointerFlags
            .HasFlag(POINTER_FLAGS.POINTER_FLAG_CANCELED));
    }

    [TestMethod]
    public void Touch_DestroyEntryPointMissing_AfterAcceptedFramesNeverReportsSuccess()
    {
        var native = new FakePointerNativeApi
        {
            DestroyException = new EntryPointNotFoundException(),
        };

        var ex = Assert.ThrowsExactly<PointerInjectionException>(() =>
            PointerInput.Touch(TouchGesture.Tap, OneTouchPath, 0, 0, native));

        Assert.AreEqual(
            "Touch frames were injected, but the command cannot report success because synthetic pointer device cleanup failed. " +
            "Synthetic pointer device cleanup also failed: DestroySyntheticPointerDevice is unavailable because the required user32.dll entry point could not be loaded.",
            ex.Message);
        Assert.AreEqual(
            "device_cleanup_failed: DestroySyntheticPointerDevice is unavailable because the required user32.dll entry point could not be loaded.",
            ex.CleanupDetails);
        Assert.AreEqual(0, native.LegacyFrames.Count);
    }

    [TestMethod]
    public void Touch_MissingRelatedModernExportAndLegacyExports_FailsDeterministically()
    {
        var native = new FakePointerNativeApi
        {
            ModernPointerInjectionAvailable = false,
            LegacyTouchInjectionAvailable = false,
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PointerInput.Touch(TouchGesture.Tap, OneTouchPath, 0, 0, native));

        Assert.AreEqual(
            "Touch injection is unavailable: the modern synthetic-pointer path could not be used " +
            "(required exports CreateSyntheticPointerDevice, InjectSyntheticPointerInput, and DestroySyntheticPointerDevice are not all available), " +
            "and the legacy touch fallback is unavailable (required exports InitializeTouchInjection and InjectTouchInput are not both available). " +
            "No touch frame was injected.",
            ex.Message);
    }

    [TestMethod]
    public void Touch_SwipeWithoutModernExports_UsesLegacyFallback()
    {
        var native = new FakePointerNativeApi
        {
            ModernPointerInjectionAvailable = false,
        };

        PointerInput.Touch(TouchGesture.Swipe, SwipePath, 0, 0, native);

        Assert.AreEqual(1, native.InitializeLegacyCallCount);
        Assert.AreEqual(22, native.LegacyFrames.Count);
        Assert.IsTrue(native.LegacyFrames[0][0].pointerInfo.pointerFlags.HasFlag(POINTER_FLAGS.POINTER_FLAG_DOWN));
        Assert.IsTrue(native.LegacyFrames[^1][0].pointerInfo.pointerFlags.HasFlag(POINTER_FLAGS.POINTER_FLAG_UP));
    }

    [TestMethod]
    public void Pen_CreateEntryPointMissing_HasNoLegacyFallback()
    {
        var native = new FakePointerNativeApi
        {
            CreateException = new EntryPointNotFoundException(),
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PointerInput.Pen([new PointerPoint(10, 20)], 0.5f, 0, 0, false, 0, native));

        Assert.AreEqual(
            "Pen injection is unavailable: CreateSyntheticPointerDevice is unavailable because the required user32.dll entry point could not be loaded. " +
            "Pen has no legacy fallback. No pen frame was injected.",
            ex.Message);
        Assert.AreEqual(0, native.InitializeLegacyCallCount);
    }

    [TestMethod]
    public void Pen_InjectDllMissingBeforeFirstFrame_HasNoLegacyFallbackAndDestroysDevice()
    {
        var native = new FakePointerNativeApi();
        native.SyntheticExceptions.Enqueue(new DllNotFoundException());

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PointerInput.Pen([new PointerPoint(10, 20)], 0.5f, 0, 0, false, 0, native));

        Assert.AreEqual(
            "Pen injection is unavailable: InjectSyntheticPointerInput is unavailable because the required user32.dll entry point could not be loaded. " +
            "Pen has no legacy fallback. No pen frame was injected.",
            ex.Message);
        Assert.AreEqual(1, native.DestroyCallCount);
        Assert.AreEqual(0, native.LegacyFrames.Count);
    }

    [TestMethod]
    public void Touch_ModernInitialFailure_CleansUpEveryContactAndNeverFallsBack()
    {
        var native = new FakePointerNativeApi();
        native.SyntheticResults.Enqueue(false);
        native.SyntheticResults.Enqueue(true);
        native.LastErrors.Enqueue(5);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PointerInput.Touch(TouchGesture.Tap, TwoTouchPaths, 0, 0, native));

        Assert.AreEqual(
            "InjectSyntheticPointerInput (touch) failed (Win32 error 5) — pointer injection is unavailable in the current Windows version, session, or integrity context.",
            ex.Message);
        Assert.AreEqual(2, native.SyntheticFrames.Count);
        Assert.AreEqual(2, native.SyntheticFrames[1].Length);
        Assert.IsTrue(native.SyntheticFrames[1].All(info =>
            info.Anonymous.touchInfo.pointerInfo.pointerFlags.HasFlag(POINTER_FLAGS.POINTER_FLAG_UP) &&
            info.Anonymous.touchInfo.pointerInfo.pointerFlags.HasFlag(POINTER_FLAGS.POINTER_FLAG_CANCELED)));
        Assert.AreEqual(0, native.LegacyFrames.Count);
        Assert.AreEqual(1, native.DestroyCallCount);
    }

    [TestMethod]
    public void Touch_ModernInitialAndCleanupFailure_ReportsBothExactly()
    {
        var native = new FakePointerNativeApi();
        native.SyntheticResults.Enqueue(false);
        native.SyntheticResults.Enqueue(false);
        native.LastErrors.Enqueue(5);
        native.LastErrors.Enqueue(31);

        var ex = Assert.ThrowsExactly<PointerInjectionException>(() =>
            PointerInput.Touch(TouchGesture.Tap, OneTouchPath, 0, 0, native));

        Assert.AreEqual(
            "InjectSyntheticPointerInput (touch) failed (Win32 error 5) — pointer injection is unavailable in the current Windows version, session, or integrity context. " +
            "Best-effort canceled-UP cleanup also failed: InjectSyntheticPointerInput (touch) failed (Win32 error 31) — pointer injection is unavailable in the current Windows version, session, or integrity context.",
            ex.Message);
        Assert.AreEqual(2, native.SyntheticFrames.Count);
        Assert.AreEqual(0, native.LegacyFrames.Count);
    }

    [TestMethod]
    public void Touch_LegacyInitialFailure_CleansUpAndNeverReportsSuccess()
    {
        var native = new FakePointerNativeApi
        {
            ModernPointerInjectionAvailable = false,
        };
        native.LegacyResults.Enqueue(false);
        native.LegacyResults.Enqueue(true);
        native.LastErrors.Enqueue(5);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PointerInput.Touch(TouchGesture.Tap, TwoTouchPaths, 0, 0, native));

        Assert.AreEqual(
            "InjectTouchInput failed (Win32 error 5) — pointer injection is unavailable in the current Windows version, session, or integrity context.",
            ex.Message);
        Assert.AreEqual(2, native.LegacyFrames.Count);
        Assert.IsTrue(native.LegacyFrames[1].All(contact =>
            contact.pointerInfo.pointerFlags.HasFlag(POINTER_FLAGS.POINTER_FLAG_CANCELED)));
    }

    [TestMethod]
    public void Pen_InitialFailure_CleansUpAtInitialPointAndNeverReportsSuccess()
    {
        var native = new FakePointerNativeApi();
        native.SyntheticResults.Enqueue(false);
        native.SyntheticResults.Enqueue(true);
        native.LastErrors.Enqueue(5);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            PointerInput.Pen([new PointerPoint(-33, 44)], 0.5f, 0, 0, false, 0, native));

        Assert.AreEqual(
            "InjectSyntheticPointerInput (pen) failed (Win32 error 5) — pointer injection is unavailable in the current Windows version, session, or integrity context.",
            ex.Message);
        Assert.AreEqual(2, native.SyntheticFrames.Count);
        var cleanup = native.SyntheticFrames[1][0].Anonymous.penInfo;
        Assert.AreEqual(-33, cleanup.pointerInfo.ptPixelLocation.X);
        Assert.AreEqual(44, cleanup.pointerInfo.ptPixelLocation.Y);
        Assert.AreEqual(0u, cleanup.pressure);
        Assert.IsTrue(cleanup.pointerInfo.pointerFlags.HasFlag(POINTER_FLAGS.POINTER_FLAG_CANCELED));
    }

    [TestMethod]
    public void Pen_InitialAndCleanupFailure_ReportsBothExactly()
    {
        var native = new FakePointerNativeApi();
        native.SyntheticResults.Enqueue(false);
        native.SyntheticResults.Enqueue(false);
        native.LastErrors.Enqueue(5);
        native.LastErrors.Enqueue(31);

        var ex = Assert.ThrowsExactly<PointerInjectionException>(() =>
            PointerInput.Pen([new PointerPoint(10, 20)], 0.5f, 0, 0, false, 0, native));

        Assert.AreEqual(
            "InjectSyntheticPointerInput (pen) failed (Win32 error 5) — pointer injection is unavailable in the current Windows version, session, or integrity context. " +
            "Best-effort canceled-UP cleanup also failed: InjectSyntheticPointerInput (pen) failed (Win32 error 31) — pointer injection is unavailable in the current Windows version, session, or integrity context.",
            ex.Message);
        Assert.AreEqual(2, native.SyntheticFrames.Count);
    }

    private sealed class FakePointerNativeApi : IPointerNativeApi
    {
        private bool _modernPointerInjectionAvailable = true;

        public bool ModernPointerInjectionAvailable
        {
            get
            {
                if (ModernAvailabilityException is not null)
                {
                    throw ModernAvailabilityException;
                }

                return _modernPointerInjectionAvailable;
            }
            set => _modernPointerInjectionAvailable = value;
        }

        public bool LegacyTouchInjectionAvailable { get; set; } = true;

        public Exception? ModernAvailabilityException { get; set; }

        public Exception? CreateException { get; set; }

        public Exception? DestroyException { get; set; }

        public Queue<Exception> SyntheticExceptions { get; } = new();

        public Dictionary<int, Exception> SyntheticExceptionsByCall { get; } = [];

        public Queue<bool> SyntheticResults { get; } = new();

        public Queue<bool> LegacyResults { get; } = new();

        public Queue<int> LastErrors { get; } = new();

        public List<POINTER_TYPE_INFO[]> SyntheticFrames { get; } = [];

        public List<POINTER_TOUCH_INFO[]> LegacyFrames { get; } = [];

        public int CreateCallCount { get; private set; }

        public int DestroyCallCount { get; private set; }

        public int InitializeLegacyCallCount { get; private set; }

        private int SyntheticCallCount { get; set; }

        public nint CreateSyntheticPointerDevice(
            POINTER_INPUT_TYPE pointerType,
            uint maxCount,
            POINTER_FEEDBACK_MODE mode)
        {
            CreateCallCount++;
            if (CreateException is not null)
            {
                throw CreateException;
            }

            return 1;
        }

        public bool InjectSyntheticPointerInput(
            nint device,
            ReadOnlySpan<POINTER_TYPE_INFO> pointerInfo)
        {
            SyntheticFrames.Add(pointerInfo.ToArray());
            SyntheticCallCount++;
            if (SyntheticExceptionsByCall.TryGetValue(SyntheticCallCount, out Exception? scheduledException))
            {
                throw scheduledException;
            }

            if (SyntheticExceptions.TryDequeue(out Exception? exception))
            {
                throw exception;
            }

            return SyntheticResults.TryDequeue(out bool result) ? result : true;
        }

        public void DestroySyntheticPointerDevice(nint device)
        {
            DestroyCallCount++;
            if (DestroyException is not null)
            {
                throw DestroyException;
            }
        }

        public bool InitializeTouchInjection(uint maxCount, TOUCH_FEEDBACK_MODE mode)
        {
            InitializeLegacyCallCount++;
            return true;
        }

        public bool InjectTouchInput(ReadOnlySpan<POINTER_TOUCH_INFO> contacts)
        {
            LegacyFrames.Add(contacts.ToArray());
            return LegacyResults.TryDequeue(out bool result) ? result : true;
        }

        public int GetLastError() => LastErrors.TryDequeue(out int error) ? error : 0;
    }
}
