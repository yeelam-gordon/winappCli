// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Pure geometry/parsing helpers shared by <c>ui touch</c> and <c>ui pen</c>. Parses the <c>x,y</c>
/// coordinate grammar (identical to <c>ui drag</c>'s) and expands a high-level touch gesture into the
/// per-finger waypoint paths that <see cref="IPointerInput.Touch"/> replays.
/// </summary>
internal static class PointerGesturePlanner
{
    /// <summary>Contact spread (px) between extra fingers for multi-finger tap/swipe gestures.</summary>
    private const int FingerSpacingPx = 24;

    /// <summary>Residual gap (px) each pinch finger keeps from the center at full contraction.</summary>
    private const int PinchCenterGapPx = 4;

    /// <summary>
    /// Maximum simultaneous touch contacts supported by this CLI and registered with both injection
    /// paths. <c>ui touch --fingers</c> is rejected above this compatibility limit.
    /// </summary>
    public const int MaxContacts = 10;

    /// <summary>
    /// Returns the first point in <paramref name="points"/> that lies outside <paramref name="rect"/>,
    /// or <see langword="null"/> when every point is inside. Used to reject touch/pen gestures whose
    /// coordinates fall outside the target window before any OS-wide injection.
    /// </summary>
    public static PointerPoint? FirstOutOfBounds(PointerRect rect, IEnumerable<PointerPoint> points)
    {
        foreach (var p in points)
        {
            if (!rect.Contains(p))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses a single signed <c>x,y</c> integer pair in physical virtual-screen pixels, as reported by
    /// <c>ui inspect</c>. Coordinates can be negative on monitors left of or above the primary display.
    /// </summary>
    public static bool TryParsePoint(string? value, out PointerPoint point)
    {
        point = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            point = new PointerPoint(x, y);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a pen ink path — a whitespace-separated list of <c>x,y</c> pairs
    /// (<c>"x1,y1 x2,y2 ..."</c>). Returns <see langword="false"/> if any token is not a valid pair.
    /// </summary>
    public static bool TryParsePath(string? value, out List<PointerPoint> points)
    {
        points = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParsePoint(token, out var p))
            {
                return false;
            }
            points.Add(p);
        }

        return points.Count > 0;
    }

    /// <summary>
    /// Expands a touch gesture into per-finger waypoint paths plus the flattened point list reported in
    /// JSON. <paramref name="start"/> is the anchor (selector center or <c>--at</c>). Pinch/stretch
    /// always produce exactly two contact paths. <paramref name="direction"/> controls the swipe axis
    /// when no explicit <paramref name="end"/> is given; defaults to <c>"right"</c>.
    /// </summary>
    public static (List<IReadOnlyList<PointerPoint>> ContactPaths, List<PointerPoint> Points, int Fingers) PlanTouch(
        TouchGesture gesture,
        PointerPoint start,
        PointerPoint? end,
        int distance,
        int fingers,
        string? direction = null)
    {
        var contactPaths = new List<IReadOnlyList<PointerPoint>>();

        switch (gesture)
        {
            case TouchGesture.Swipe:
            {
                var to = end ?? ComputeSwipeEnd(start, distance, direction);
                int count = Math.Max(1, fingers);
                for (int i = 0; i < count; i++)
                {
                    int dy = i * FingerSpacingPx;
                    contactPaths.Add([
                        new PointerPoint(start.X, checked(start.Y + dy)),
                        new PointerPoint(to.X, checked(to.Y + dy))
                    ]);
                }
                break;
            }

            case TouchGesture.Pinch:
            case TouchGesture.Stretch:
            {
                int half = Math.Max(PinchCenterGapPx + 1, distance / 2);

                // Two opposing fingers along the x-axis. Pinch converges toward the center; stretch
                // diverges away from it.
                var leftApart = new PointerPoint(checked(start.X - half), start.Y);
                var rightApart = new PointerPoint(checked(start.X + half), start.Y);
                var leftNear = new PointerPoint(checked(start.X - PinchCenterGapPx), start.Y);
                var rightNear = new PointerPoint(checked(start.X + PinchCenterGapPx), start.Y);

                if (gesture == TouchGesture.Pinch)
                {
                    contactPaths.Add([leftApart, leftNear]);
                    contactPaths.Add([rightApart, rightNear]);
                }
                else
                {
                    contactPaths.Add([leftNear, leftApart]);
                    contactPaths.Add([rightNear, rightApart]);
                }
                break;
            }

            case TouchGesture.Tap:
            case TouchGesture.DoubleTap:
            case TouchGesture.LongPress:
            default:
            {
                int count = Math.Max(1, fingers);
                for (int i = 0; i < count; i++)
                {
                    contactPaths.Add([new PointerPoint(checked(start.X + i * FingerSpacingPx), start.Y)]);
                }
                break;
            }
        }

        var points = new List<PointerPoint>();
        foreach (var path in contactPaths)
        {
            points.AddRange(path);
        }

        return (contactPaths, points, contactPaths.Count);
    }

    /// <summary>
    /// Computes a swipe end point from <paramref name="start"/> by moving <paramref name="distance"/>
    /// pixels along <paramref name="direction"/> (right/left/up/down; defaults to right).
    /// </summary>
    private static PointerPoint ComputeSwipeEnd(PointerPoint start, int distance, string? direction)
    {
        return (direction ?? "right").ToLowerInvariant() switch
        {
            "left"  => new PointerPoint(checked(start.X - distance), start.Y),
            "up"    => new PointerPoint(start.X, checked(start.Y - distance)),
            "down"  => new PointerPoint(start.X, checked(start.Y + distance)),
            _       => new PointerPoint(checked(start.X + distance), start.Y), // "right" + default
        };
    }
}
