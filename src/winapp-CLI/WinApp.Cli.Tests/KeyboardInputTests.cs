// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace WinApp.Cli.Tests;

[TestClass]
public class KeyboardInputTests
{
    [TestMethod]
    public void Send_ZeroTarget_FailsBeforeEitherTransport()
    {
        foreach (var transport in new[] { KeyTransport.PostMessage, KeyTransport.SendInput })
        {
            var error = Assert.ThrowsExactly<KeyboardInjectionException>(
                () => KeyboardInput.Send(0, KeyStringParser.Parse("enter"), transport));

            Assert.AreEqual(UiJsonError.CodeNoTargetWindow, error.Code);
        }
    }

    [TestMethod]
    public void Send_StaleTarget_IsRejectedBeforePosting()
    {
        var error = Assert.ThrowsExactly<KeyboardInjectionException>(
            () => KeyboardInput.Send(
                0x7FFFFFFF,
                KeyStringParser.Parse("enter"),
                KeyTransport.PostMessage));

        Assert.AreEqual(UiJsonError.CodeNoTargetWindow, error.Code);
        StringAssert.Contains(error.Message, "not a live window");
    }

    [TestMethod]
    public void PostMessage_AccessDenied_ExplainsUipiDirection()
    {
        var error = KeyboardInput.CreatePostMessageException(5, "key-down");

        Assert.AreEqual(UiJsonError.CodeInputInjectionFailed, error.Code);
        StringAssert.Contains(error.Message, "UIPI");
        StringAssert.Contains(error.Message, "equal- or lower-integrity");
    }

    [TestMethod]
    [DataRow("lshift", (ushort)0x10, (byte)0x2A, false)]
    [DataRow("rshift", (ushort)0x10, (byte)0x36, false)]
    [DataRow("lctrl", (ushort)0x11, (byte)0x1D, false)]
    [DataRow("rctrl", (ushort)0x11, (byte)0x1D, true)]
    [DataRow("lalt", (ushort)0x12, (byte)0x38, false)]
    [DataRow("ralt", (ushort)0x12, (byte)0x38, true)]
    public void PostMessage_SidedModifier_UsesGenericWParamAndSidedMetadata(
        string modifier,
        ushort expectedWParam,
        byte expectedScanCode,
        bool expectedExtended)
    {
        var posted = new List<(uint Message, nuint WParam, uint LParam)>();

        KeyboardInput.SendViaPostMessage(
            new HWND(1),
            KeyStringParser.Parse($"{modifier}+a"),
            default,
            CapturePostMessage);

        var keyDown = posted[0];
        Assert.AreEqual((nuint)expectedWParam, keyDown.WParam);
        Assert.AreEqual(expectedScanCode, (byte)((keyDown.LParam >> 16) & 0xFF));
        Assert.AreEqual(expectedExtended, (keyDown.LParam & (1u << 24)) != 0);

        bool CapturePostMessage(
            HWND _,
            uint message,
            WPARAM wParam,
            LPARAM lParam,
            out int error)
        {
            error = 0;
            posted.Add((message, (nuint)wParam, unchecked((uint)(nint)lParam)));
            return true;
        }
    }

    [TestMethod]
    public void PostMessage_UnexpectedFailure_ReleasesTrackedModifierAndRethrows()
    {
        var posted = new List<(uint Message, nuint WParam, uint LParam)>();
        var expected = new InvalidOperationException("Synthetic PostMessage failure.");

        var actual = Assert.ThrowsExactly<InvalidOperationException>(
            () => KeyboardInput.SendViaPostMessage(
                new HWND(1),
                KeyStringParser.Parse("rctrl+a"),
                default,
                FailSecondPostMessage));

        Assert.AreSame(expected, actual);
        Assert.AreEqual(3, posted.Count);
        Assert.AreEqual(PInvoke.WM_KEYDOWN, posted[0].Message);
        Assert.AreEqual((nuint)0x11, posted[0].WParam);
        Assert.IsTrue((posted[0].LParam & (1u << 24)) != 0);
        Assert.AreEqual(PInvoke.WM_KEYUP, posted[2].Message);
        Assert.AreEqual((nuint)0x11, posted[2].WParam);
        Assert.IsTrue((posted[2].LParam & (1u << 24)) != 0);
        Assert.IsTrue((posted[2].LParam & (1u << 30)) != 0);
        Assert.IsTrue((posted[2].LParam & (1u << 31)) != 0);

        bool FailSecondPostMessage(
            HWND _,
            uint message,
            WPARAM wParam,
            LPARAM lParam,
            out int error)
        {
            error = 0;
            posted.Add((message, (nuint)wParam, unchecked((uint)(nint)lParam)));
            if (posted.Count == 2)
            {
                throw expected;
            }

            return true;
        }
    }

    [TestMethod]
    public void PostMessage_UnexpectedFailureAndCleanupException_UsesInjectionFailure()
    {
        var posted = new List<(uint Message, nuint WParam)>();
        int postCount = 0;
        var error = Assert.ThrowsExactly<KeyboardInjectionException>(
            () => KeyboardInput.SendViaPostMessage(
                new HWND(1),
                KeyStringParser.Parse("rctrl+rshift+a"),
                default,
                ThrowDuringDeliveryAndCleanup));

        Assert.AreEqual(UiJsonError.CodeInputInjectionFailed, error.Code);
        Assert.AreEqual(
            "Synthetic PostMessage failure. Best-effort PostMessage key-up cleanup also failed for " +
            "1 key(s), including InvalidOperationException during cleanup; " +
            "the target may have observed a partial sequence.",
            error.Message);
        Assert.IsInstanceOfType<AggregateException>(error.InnerException);
        Assert.AreEqual(5, posted.Count);
        Assert.AreEqual(PInvoke.WM_KEYUP, posted[4].Message);
        Assert.AreEqual((nuint)0x11, posted[4].WParam);

        bool ThrowDuringDeliveryAndCleanup(
            HWND _,
            uint message,
            WPARAM wParam,
            LPARAM ____,
            out int nativeError)
        {
            nativeError = 0;
            postCount++;
            posted.Add((message, (nuint)wParam));
            if (postCount <= 2 || postCount == 5)
            {
                return true;
            }

            throw new InvalidOperationException(
                postCount == 3
                    ? "Synthetic PostMessage failure."
                    : "Synthetic cleanup failure.");
        }
    }

    [TestMethod]
    public void BuildReleaseInputs_ZeroDelivery_DoesNotReleaseUninjectedKeys()
    {
        var attempted = new[]
        {
            KeyboardInput.KeyEvent(0x11, extended: false, keyUp: false),
            KeyboardInput.KeyEvent(0x41, extended: false, keyUp: false),
        };

        Assert.AreEqual(0, KeyboardInput.BuildReleaseInputs(attempted, deliveredCount: 0).Length);
    }

    [TestMethod]
    public void BuildReleaseInputs_ReleasesOnlyKeysHeldByDeliveredPrefix()
    {
        var attempted = new[]
        {
            KeyboardInput.KeyEvent(0x11, extended: false, keyUp: false),
            KeyboardInput.KeyEvent(0x41, extended: false, keyUp: false),
            KeyboardInput.KeyEvent(0x41, extended: false, keyUp: true),
            KeyboardInput.KeyEvent(0x11, extended: false, keyUp: true),
        };

        var afterTwo = KeyboardInput.BuildReleaseInputs(attempted, deliveredCount: 2);
        Assert.AreEqual(2, afterTwo.Length);
        Assert.AreEqual((VIRTUAL_KEY)0x41, afterTwo[0].Anonymous.ki.wVk);
        Assert.AreEqual((VIRTUAL_KEY)0x11, afterTwo[1].Anonymous.ki.wVk);

        var afterThree = KeyboardInput.BuildReleaseInputs(attempted, deliveredCount: 3);
        Assert.AreEqual(1, afterThree.Length);
        Assert.AreEqual((VIRTUAL_KEY)0x11, afterThree[0].Anonymous.ki.wVk);

        Assert.AreEqual(0, KeyboardInput.BuildReleaseInputs(attempted, deliveredCount: 4).Length);
    }

    [TestMethod]
    public void ShortWrite_CompletePairPrefix_ReportsNoHeldKeysAndInsertedCount()
    {
        var attempted = new[]
        {
            KeyboardInput.KeyEvent(0x41, extended: false, keyUp: false),
            KeyboardInput.KeyEvent(0x41, extended: false, keyUp: true),
            KeyboardInput.KeyEvent(0x42, extended: false, keyUp: false),
            KeyboardInput.KeyEvent(0x42, extended: false, keyUp: true),
        };

        const uint deliveredCount = 2;
        var releases = KeyboardInput.BuildReleaseInputs(attempted, deliveredCount);
        var diagnostic = KeyboardInput.DescribeSendInputCleanup(
            deliveredCount,
            (uint)attempted.Length,
            releases.Length,
            releasedCount: 0);

        Assert.AreEqual(0, releases.Length);
        Assert.AreEqual(
            "The delivered prefix inserted 2 of 4 key events and left no injected keys held down. " +
            "Those events were already applied; do not blindly retry the full gesture.",
            diagnostic);
    }

    [TestMethod]
    public void ShortWrite_ZeroInsertedEvents_ReportsFullRetrySafe()
    {
        Assert.AreEqual(
            "No key events were inserted, so no synthetic key-up was needed; retrying the full gesture is safe.",
            KeyboardInput.DescribeSendInputCleanup(
                deliveredCount: 0,
                attemptedCount: 4,
                releaseCount: 0,
                releasedCount: 0));
    }
}
