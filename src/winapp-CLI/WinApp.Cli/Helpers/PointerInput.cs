// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.Pointer;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Injects synthetic touch and pen input using the Windows pointer-injection APIs. Touch prefers the
/// synthetic-pointer device (<c>CreateSyntheticPointerDevice(PT_TOUCH)</c>/
/// <c>InjectSyntheticPointerInput</c>) — the same mechanism the pen path uses — after verifying all
/// related exports, and falls back to the legacy
/// <c>InitializeTouchInjection</c>/<c>InjectTouchInput</c> API when the modern path is unavailable
/// before any frame is accepted. Pen uses <c>CreateSyntheticPointerDevice(PT_PEN)</c> and has no
/// legacy fallback. Coordinates are screen pixels — the same space <c>ui inspect</c> reports.
/// </summary>
internal static partial class PointerInput
{
    /// <summary>
    /// Maximum simultaneous touch contacts supported by this CLI. This intentionally matches the
    /// ten-contact configuration used for both the synthetic and legacy injection paths.
    /// </summary>
    private const uint MaxContacts = 10;

    /// <summary>Pen pressure range used by the pointer APIs (0..1024).</summary>
    private const uint PenPressureMax = 1024;

    /// <summary>Steps used to interpolate a moving gesture between two waypoints.</summary>
    private const int GlideSteps = 20;

    /// <summary>
    /// Interval in milliseconds between stationary UPDATE frames emitted during a touch long-press hold.
    /// Windows touch injection can cancel or mis-classify a held contact that receives no periodic
    /// frames; this cadence keeps the contact alive and lets the OS recognise it as press-and-hold.
    /// </summary>
    internal const int HoldFrameIntervalMs = 40;

    // touchFlags / touchMask / penFlags / penMask are raw DWORD bitmasks in the generated structs.
    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    private const uint PEN_FLAG_NONE = 0x00000000;
    private const uint PEN_FLAG_ERASER = 0x00000004;
    private const uint PEN_MASK_PRESSURE = 0x00000001;
    private const uint PEN_MASK_TILT_X = 0x00000004;
    private const uint PEN_MASK_TILT_Y = 0x00000008;

    /// <summary>Delegate that submits one frame of touch contacts (synthetic device or legacy API).</summary>
    internal delegate void TouchSender(POINTER_TOUCH_INFO[] contacts);

    /// <summary>
    /// Win32 error code 21 (ERROR_NOT_READY): the touch-injection subsystem is temporarily
    /// busy processing a previous frame. Calls less than ~0.1ms apart can return this code
    /// and must retry the identical frame rather than treating it as permanent failure.
    /// </summary>
    private const int ErrorNotReady = 21;

    /// <summary>Maximum number of ERROR_NOT_READY retries per frame before giving up.</summary>
    internal const int MaxErrorNotReadyRetries = 10;

    /// <summary>
    /// Submits one touch frame, retrying up to <see cref="MaxErrorNotReadyRetries"/> times when
    /// Win32 error 21 (ERROR_NOT_READY) is returned. Any other error propagates immediately.
    /// The final attempt is unguarded so the exception surfaces after all retries are exhausted.
    /// </summary>
    private static void SendFrameWithRetry(TouchSender send, POINTER_TOUCH_INFO[] contacts)
    {
        bool priorNativeCallMayHaveBeenAttempted = false;
        for (int attempt = 0; attempt < MaxErrorNotReadyRetries; attempt++)
        {
            try
            {
                send(contacts);
                return;
            }
            catch (InvalidOperationException ex) when (IsWin32ErrorNotReady(ex))
            {
                priorNativeCallMayHaveBeenAttempted = true;
                Thread.Sleep(1);
            }
            catch (PointerNativeApiUnavailableException ex) when (priorNativeCallMayHaveBeenAttempted)
            {
                throw ex.WithPriorNativeCallAttempt();
            }
        }

        try
        {
            send(contacts); // final attempt — let any exception propagate
        }
        catch (PointerNativeApiUnavailableException ex) when (priorNativeCallMayHaveBeenAttempted)
        {
            throw ex.WithPriorNativeCallAttempt();
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="ex"/> was thrown because the
    /// touch-injection API returned Win32 error 21 (ERROR_NOT_READY). The message format
    /// produced by <see cref="SendLegacyTouch"/> and <see cref="SendSyntheticTouch"/> includes
    /// <c>"Win32 error 21"</c>.
    /// </summary>
    internal static bool IsWin32ErrorNotReady(InvalidOperationException ex)
        => ex.Message.Contains("Win32 error 21", StringComparison.Ordinal);

    /// <summary>
    /// Runs the touch gesture loop (handles double-tap repetition) against the given
    /// <paramref name="send"/> delegate. Exposed internally so tests can inject a recording
    /// sender and an injectable inter-tap sleep to assert the repetition contract.
    /// </summary>
    internal static void RunTouchGesture(
        TouchGesture gesture,
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs,
        TouchSender send,
        Action<int>? sleepInter = null)
    {
        var sleepFn = sleepInter ?? Thread.Sleep;
        int repeats = gesture == TouchGesture.DoubleTap ? 2 : 1;
        for (int r = 0; r < repeats; r++)
        {
            InjectTouchStroke(contactPaths, holdMs, durationMs,
                (contacts) => SendFrameWithRetry(send, contacts));
            if (r + 1 < repeats)
            {
                sleepFn(60); // inter-tap gap for double-tap
            }
        }
    }

    internal static void InjectTouchStroke(
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs,
        TouchSender send,
        Action<int>? sleep = null,
        Func<long>? nowMs = null)
    {
        int count = contactPaths.Count;
        var contacts = new POINTER_TOUCH_INFO[count];
        var lastSentPoints = new PointerPoint[count];

        // --- Press down ---
        for (int i = 0; i < count; i++)
        {
            var start = contactPaths[i][0];
            lastSentPoints[i] = start;
            contacts[i] = MakeContact(
                (uint)i, start.X, start.Y,
                POINTER_FLAGS.POINTER_FLAG_DOWN | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                primary: i == 0);
        }
        bool contactMayBeActive = true;
        try
        {
            try
            {
                send(contacts);
            }
            catch (PointerNativeApiUnavailableException ex)
                when (!ex.PriorNativeCallMayHaveBeenAttempted)
            {
                // The source-generated interop stub failed before entering user32, so the
                // initial DOWN was not submitted and there is nothing to cancel.
                contactMayBeActive = false;
                throw;
            }

            // --- Hold phase: emit periodic stationary UPDATE frames so Windows does not drop/cancel
            //     the contact or mis-classify the hold as a tap. One frame per HoldFrameIntervalMs,
            //     clamping the final partial interval so the total hold ≈ holdMs. ---
            if (holdMs > 0)
            {
                int elapsed = 0;
                while (elapsed < holdMs)
                {
                    int interval = Math.Min(HoldFrameIntervalMs, holdMs - elapsed);
                    Thread.Sleep(interval);
                    elapsed += interval;

                    for (int i = 0; i < count; i++)
                    {
                        var start = contactPaths[i][0];
                        contacts[i] = MakeContact(
                            (uint)i, start.X, start.Y,
                            POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                            primary: i == 0);
                    }
                    send(contacts);
                }
            }

            // --- Glide between waypoints (only if any contact has more than one waypoint) ---
            int maxWaypoints = 0;
            foreach (var path in contactPaths)
            {
                maxWaypoints = Math.Max(maxWaypoints, path.Count);
            }

            if (maxWaypoints > 1)
            {
                if (durationMs > 0)
                {
                    // Cumulative-timestamp scheduling (mirrors InjectPenStroke): for frame index k (1..N),
                    // the target offset is targetMs_k = durationMs * k / N. Before frame k sleep only
                    // max(0, targetMs_k − elapsed) so total wall time ≈ durationMs regardless of step count.
                    // No Math.Max(1,…) floor → --duration-ms 1 stays ≈ 1 ms total. The final frame (k == N)
                    // has targetMs == durationMs and is followed by no trailing sleep.
                    var sleepFn = sleep ?? Thread.Sleep;
                    var sw = Stopwatch.StartNew();
                    var nowFn = nowMs ?? (() => sw.ElapsedMilliseconds);

                    ScheduleGlide(durationMs, GlideSteps, frameIndex =>
                    {
                        double t = frameIndex / (double)GlideSteps;
                        for (int i = 0; i < count; i++)
                        {
                            var path = contactPaths[i];
                            var (x, y) = Interpolate(path, t);
                            contacts[i] = MakeContact(
                                (uint)i, x, y,
                                POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                                primary: i == 0);
                        }
                        send(contacts);
                        for (int i = 0; i < count; i++)
                        {
                            lastSentPoints[i] = new PointerPoint(
                                contacts[i].pointerInfo.ptPixelLocation.X,
                                contacts[i].pointerInfo.ptPixelLocation.Y);
                        }
                    }, sleepFn, nowFn);
                }
                else
                {
                    // durationMs <= 0: no timing — send all glide frames immediately.
                    for (int step = 1; step <= GlideSteps; step++)
                    {
                        double t = step / (double)GlideSteps;
                        for (int i = 0; i < count; i++)
                        {
                            var path = contactPaths[i];
                            var (x, y) = Interpolate(path, t);
                            contacts[i] = MakeContact(
                                (uint)i, x, y,
                                POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                                primary: i == 0);
                        }
                        send(contacts);
                        for (int i = 0; i < count; i++)
                        {
                            lastSentPoints[i] = new PointerPoint(
                                contacts[i].pointerInfo.ptPixelLocation.X,
                                contacts[i].pointerInfo.ptPixelLocation.Y);
                        }
                    }
                }
            }

            // --- Lift on the normal path: let failure propagate so the caller knows the pointer
            //     may be stuck and the command exits non-zero with a structured error. ---
            for (int i = 0; i < count; i++)
            {
                var last = lastSentPoints[i];
                contacts[i] = MakeContact((uint)i, last.X, last.Y, POINTER_FLAGS.POINTER_FLAG_UP, primary: i == 0);
            }
            send(contacts);
            contactMayBeActive = false;
        }
        catch (Exception primaryFailure)
        {
            Exception? cancellationFailure = null;
            if (contactMayBeActive)
            {
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        var last = lastSentPoints[i];
                        contacts[i] = MakeContact(
                            (uint)i, last.X, last.Y,
                            POINTER_FLAGS.POINTER_FLAG_UP | POINTER_FLAGS.POINTER_FLAG_CANCELED,
                            primary: i == 0);
                    }
                    send(contacts);
                }
                catch (Exception ex)
                {
                    cancellationFailure = ex;
                }
            }

            if (cancellationFailure is not null)
            {
                throw PointerInjectionException.Combine(primaryFailure, cancellationFailure);
            }

            throw;
        }
    }

    private static POINTER_TOUCH_INFO MakeContact(uint id, int x, int y, POINTER_FLAGS flags, bool primary)
    {
        if (primary)
        {
            flags |= POINTER_FLAGS.POINTER_FLAG_PRIMARY;
        }

        return new POINTER_TOUCH_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = POINTER_INPUT_TYPE.PT_TOUCH,
                pointerId = id,
                pointerFlags = flags,
                ptPixelLocation = new System.Drawing.Point(x, y),
            },
            touchFlags = 0,
            touchMask = TOUCH_MASK_CONTACTAREA,
            rcContact = new RECT { left = x - 2, top = y - 2, right = x + 2, bottom = y + 2 },
        };
    }

    /// <summary>
    /// Delegate that submits one pen frame. <paramref name="pressure"/> is the raw 0..1024 pressure
    /// value (0 for UP frames); <paramref name="flags"/> carries DOWN/UPDATE/UP and contact flags.
    /// </summary>
    internal delegate void PenFrameSender(int x, int y, uint pressure, POINTER_FLAGS flags);

    /// <summary>
    /// Sends the full sequence of DOWN → interpolated UPDATE glide → UP frames for a pen stroke.
    /// Exposed as <see langword="internal"/> for frame-sequence unit tests (no live device needed).
    /// </summary>
    /// <param name="sleep">
    /// Optional sleep function; defaults to <see cref="Thread.Sleep(int)"/>. Inject a fake for tests
    /// so timing assertions complete instantly without real blocking.
    /// </param>
    /// <param name="nowMs">
    /// Optional monotonic clock returning elapsed milliseconds from the start of the timed glide.
    /// Defaults to a <see cref="Stopwatch"/> started when the timed loop begins. Inject a fake for
    /// tests that need to control the apparent clock (should advance by the same amounts as <paramref name="sleep"/>).
    /// </param>
    internal static void InjectPenStroke(
        IReadOnlyList<PointerPoint> path,
        uint contactPressure,
        int durationMs,
        PenFrameSender send,
        Action<int>? sleep = null,
        Func<long>? nowMs = null)
    {
        var first = path[0];
        var lastSent = first;

        bool contactMayBeActive = true;
        try
        {
            try
            {
                send(first.X, first.Y, contactPressure,
                    POINTER_FLAGS.POINTER_FLAG_DOWN | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT);
            }
            catch (PointerNativeApiUnavailableException)
            {
                contactMayBeActive = false;
                throw;
            }

            int segments = path.Count - 1;
            if (segments > 0)
            {
                if (durationMs > 0)
                {
                    // Cumulative-timestamp scheduling via the shared ScheduleGlide helper.
                    // For frame index k (1..N), targetMs_k = durationMs * k / N; sleep only the drift-
                    // corrected delta before each frame. The final frame has targetMs == durationMs and
                    // is followed by no trailing sleep. See ScheduleGlide for the algorithm details.
                    var sleepFn = sleep ?? Thread.Sleep;
                    var sw = Stopwatch.StartNew();
                    var nowFn = nowMs ?? (() => sw.ElapsedMilliseconds);

                    int totalFrames = segments * GlideSteps;

                    ScheduleGlide(durationMs, totalFrames, frameIndex =>
                    {
                        int segIdx = (frameIndex - 1) / GlideSteps;
                        int step   = (frameIndex - 1) % GlideSteps + 1;
                        double t = step / (double)GlideSteps;
                        var from = path[segIdx];
                        var to   = path[segIdx + 1];
                        int x = (int)Math.Round(from.X + (((double)to.X - from.X) * t));
                        int y = (int)Math.Round(from.Y + (((double)to.Y - from.Y) * t));
                        send(x, y, contactPressure,
                            POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT);
                        lastSent = new PointerPoint(x, y);
                    }, sleepFn, nowFn);
                }
                else
                {
                    // durationMs <= 0: fall back to a fixed ~10 ms per waypoint (original cadence).
                    for (int i = 1; i < path.Count; i++)
                    {
                        var pt = path[i];
                        send(pt.X, pt.Y, contactPressure,
                            POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT);
                        lastSent = pt;
                        Thread.Sleep(10);
                    }
                }
            }
            // --- Lift on the normal path: let failure propagate so the caller knows the pen
            //     may be stuck and the command exits non-zero with a structured error. ---
            send(lastSent.X, lastSent.Y, 0, POINTER_FLAGS.POINTER_FLAG_UP);
            contactMayBeActive = false;
        }
        catch (Exception primaryFailure)
        {
            Exception? cancellationFailure = null;
            if (contactMayBeActive)
            {
                try
                {
                    send(lastSent.X, lastSent.Y, 0,
                        POINTER_FLAGS.POINTER_FLAG_UP | POINTER_FLAGS.POINTER_FLAG_CANCELED);
                }
                catch (Exception ex)
                {
                    cancellationFailure = ex;
                }
            }

            if (cancellationFailure is not null)
            {
                throw PointerInjectionException.Combine(primaryFailure, cancellationFailure);
            }

            throw;
        }
    }

    private static (int X, int Y) Interpolate(IReadOnlyList<PointerPoint> path, double t)
    {
        if (path.Count == 1)
        {
            return (path[0].X, path[0].Y);
        }

        // Treat the path as a single straight segment from first to last waypoint.
        var a = path[0];
        var b = path[^1];
        int x = (int)Math.Round(a.X + (((double)b.X - a.X) * t));
        int y = (int)Math.Round(a.Y + (((double)b.Y - a.Y) * t));
        return (x, y);
    }

    /// <summary>
    /// Drift-corrected sleep scheduling for a timed glide of <paramref name="totalFrames"/> evenly-spaced
    /// frames over <paramref name="durationMs"/> milliseconds. For frame index k (1..N) the target offset
    /// is <c>durationMs * k / N</c>; before frame k we sleep only <c>max(0, targetMs_k − elapsed)</c>
    /// (no <c>Math.Max(1,…)</c> floor, so sub-ms-per-frame durations yield zero sleep). The final frame
    /// (k == N) has targetMs == durationMs and is sent with no trailing sleep after it.
    /// </summary>
    private static void ScheduleGlide(
        int durationMs,
        int totalFrames,
        Action<int> sendFrame,
        Action<int> sleepFn,
        Func<long> nowFn)
    {
        for (int k = 1; k <= totalFrames; k++)
        {
            long targetMs = (long)durationMs * k / totalFrames;
            long elapsedMs = nowFn();
            int deltaMs = (int)Math.Max(0L, targetMs - elapsedMs);
            if (deltaMs > 0) { sleepFn(deltaMs); }
            sendFrame(k);
        }
    }
}
