// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;
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
