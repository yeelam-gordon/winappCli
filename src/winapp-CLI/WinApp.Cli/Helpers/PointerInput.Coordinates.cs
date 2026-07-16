// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.Pointer;

namespace WinApp.Cli.Helpers;

internal static partial class PointerInput
{
    private static void ValidateModernTouchCoordinates(
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        (int X, int Y) virtualScreenOrigin)
    {
        foreach (IReadOnlyList<PointerPoint> path in contactPaths)
        {
            foreach (PointerPoint point in path)
            {
                NormalizeModernCoordinate(point.X, virtualScreenOrigin.X, "touch x");
                NormalizeModernCoordinate(point.Y, virtualScreenOrigin.Y, "touch y");
                NormalizeModernCoordinate(
                    (long)point.X - TouchContactHalfSize,
                    virtualScreenOrigin.X,
                    "touch contact left");
                NormalizeModernCoordinate(
                    (long)point.X + TouchContactHalfSize,
                    virtualScreenOrigin.X,
                    "touch contact right");
                NormalizeModernCoordinate(
                    (long)point.Y - TouchContactHalfSize,
                    virtualScreenOrigin.Y,
                    "touch contact top");
                NormalizeModernCoordinate(
                    (long)point.Y + TouchContactHalfSize,
                    virtualScreenOrigin.Y,
                    "touch contact bottom");
            }
        }
    }

    private static void ValidateModernPenCoordinates(
        IReadOnlyList<PointerPoint> path,
        (int X, int Y) virtualScreenOrigin)
    {
        foreach (PointerPoint point in path)
        {
            NormalizeModernCoordinate(point.X, virtualScreenOrigin.X, "pen x");
            NormalizeModernCoordinate(point.Y, virtualScreenOrigin.Y, "pen y");
        }
    }

    private static POINTER_TOUCH_INFO NormalizeModernTouchContact(
        POINTER_TOUCH_INFO contact,
        (int X, int Y) virtualScreenOrigin)
    {
        contact.pointerInfo.ptPixelLocation = new System.Drawing.Point(
            NormalizeModernCoordinate(
                contact.pointerInfo.ptPixelLocation.X,
                virtualScreenOrigin.X,
                "touch x"),
            NormalizeModernCoordinate(
                contact.pointerInfo.ptPixelLocation.Y,
                virtualScreenOrigin.Y,
                "touch y"));
        contact.rcContact = new RECT
        {
            left = NormalizeModernCoordinate(
                contact.rcContact.left,
                virtualScreenOrigin.X,
                "touch contact left"),
            top = NormalizeModernCoordinate(
                contact.rcContact.top,
                virtualScreenOrigin.Y,
                "touch contact top"),
            right = NormalizeModernCoordinate(
                contact.rcContact.right,
                virtualScreenOrigin.X,
                "touch contact right"),
            bottom = NormalizeModernCoordinate(
                contact.rcContact.bottom,
                virtualScreenOrigin.Y,
                "touch contact bottom"),
        };
        return contact;
    }

    private static int NormalizeModernCoordinate(
        long absoluteCoordinate,
        int virtualScreenOrigin,
        string coordinateName)
    {
        long normalizedCoordinate = checked(absoluteCoordinate - virtualScreenOrigin);
        if (normalizedCoordinate is < int.MinValue or > int.MaxValue)
        {
            throw new PointerCoordinateNormalizationException(
                coordinateName,
                absoluteCoordinate,
                virtualScreenOrigin);
        }

        return (int)normalizedCoordinate;
    }
}
