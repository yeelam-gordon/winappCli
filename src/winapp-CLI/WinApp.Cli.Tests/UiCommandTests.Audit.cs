// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    public async Task Audit_WithoutApp_ReturnsError()
    {
        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Audit_MissingSelectorTarget_HumanReturnsElementNotFound()
    {
        _fakeUia.InspectResult = [];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["#Missing", "-a", "TestApp"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(
            ConsoleStdErr.ToString(),
            "No element found matching '#Missing'. The UI may have changed");
        Assert.AreEqual(string.Empty, TestAnsiConsole.Output);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Audit_MissingSelectorTarget_JsonReturnsExactElementNotFound()
    {
        _fakeUia.InspectResult = [];
        var originalError = Console.Error;
        using var jsonError = new StringWriter();
        try
        {
            Console.SetError(jsonError);
            var command = GetRequiredService<UiAuditCommand>();
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command,
                ["#Missing", "-a", "TestApp", "--json"]);

            Assert.AreEqual(1, exitCode);
            using var payload = System.Text.Json.JsonDocument.Parse(jsonError.ToString());
            var error = payload.RootElement.GetProperty("error");
            Assert.AreEqual("element_not_found", error.GetProperty("code").GetString());
            Assert.AreEqual(
                "No element found matching '#Missing'",
                error.GetProperty("message").GetString());
            Assert.AreEqual("#Missing", error.GetProperty("selector").GetString());
            Assert.AreEqual(string.Empty, TestAnsiConsole.Output);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    public async Task Audit_MissingName_FailsAndExitsNonZero()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Window", Name = "App", IsEnabled = true },
            new UiElement { Id = "e1", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = true, Selector = "btn-x" },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "names", "--json"]);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"ruleId\": \"names\"");
        StringAssert.Contains(output, "\"severity\": \"fail\"");
        StringAssert.Contains(output, "\"fail\": 1");
    }

    [TestMethod]
    public async Task Audit_CleanTree_PassesAndExitsZero()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Window", Name = "App", IsEnabled = true },
            new UiElement { Id = "e1", Type = "Button", Name = "OK", IsEnabled = true, IsKeyboardFocusable = true, IsInvokable = true },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "names", "--area", "keyboard", "--area", "roles", "--json"]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"fail\": 0");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Audit_InvalidLevel_ReturnsError()
    {
        var originalError = Console.Error;
        using var jsonError = new StringWriter();
        try
        {
            Console.SetError(jsonError);
            var command = GetRequiredService<UiAuditCommand>();
            var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--level", "deep", "--json"]);
            Assert.AreEqual(1, exitCode);
            StringAssert.Contains(jsonError.ToString(), "\"code\": \"invalid_arguments\"");
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    public async Task Audit_WritesReportFile()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e1", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = true, Selector = "btn-x" },
        ];

        var reportPath = Path.Combine(_tempDirectory.FullName, "audit.json");
        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "names", "--json", "-o", reportPath]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(File.Exists(reportPath), "expected report file to be written");
        var content = await File.ReadAllTextAsync(reportPath, TestContext.CancellationToken);
        StringAssert.Contains(content, "\"ruleId\": \"names\"");
    }

    [TestMethod]
    public async Task Audit_ContrastCaptureUnavailable_FailsClosed()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Text", Name = "Hello", Width = 100, Height = 16 },
        ];
        _fakeUia.WindowCaptureException = new InvalidOperationException("no capture");

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "contrast", "--json"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"fail\": 1");
        StringAssert.Contains(TestAnsiConsole.Output, "window capture or bounded pixel analysis did not complete");
        StringAssert.Contains(TestAnsiConsole.Output, "\"attempted\": 1");
        StringAssert.Contains(TestAnsiConsole.Output, "\"measured\": 0");
        StringAssert.Contains(TestAnsiConsole.Output, "\"unmeasured\": 1");
    }

    [TestMethod]
    public async Task Audit_ContrastVisibleTextWithInvalidBounds_FailsClosed()
    {
        _fakeUia.InspectResult =
        [
            new UiElement
            {
                Id = "e0", Type = "Text", Name = "Hello", Width = 0, Height = 16, Selector = "hello",
            },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--area", "contrast", "--json"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"selector\": \"hello\"");
        StringAssert.Contains(TestAnsiConsole.Output, "\"attempted\": 1");
        StringAssert.Contains(TestAnsiConsole.Output, "\"measured\": 0");
        StringAssert.Contains(TestAnsiConsole.Output, "\"unmeasured\": 1");
    }

    [TestMethod]
    public async Task Audit_ContrastSuccessfulCaptureWithNoMeasurableRatios_FailsClosed()
    {
        _fakeSession.SessionResult = new UiSessionInfo
        {
            ProcessId = 1234,
            ProcessName = "TestApp",
            WindowTitle = "Test Window",
            WindowHandle = 100,
        };

        const int w = 20, h = 20;
        var buf = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            buf[i * 4 + 0] = 255;
            buf[i * 4 + 1] = 255;
            buf[i * 4 + 2] = 255;
            buf[i * 4 + 3] = 255;
        }
        _fakeUia.WindowCaptureResult = (buf, w, h, 0, 0);
        _fakeUia.InspectResult =
        [
            new UiElement
            {
                Id = "e0", Type = "Text", Name = "Uniform", Width = w, Height = h,
                WindowHandle = 100, Selector = "uniform",
            },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--area", "contrast", "--json"]);

        var output = TestAnsiConsole.Output;
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(output, "\"fail\": 1");
        StringAssert.Contains(output, "\"warn\": 0");
        StringAssert.Contains(output, "\"attempted\": 1");
        StringAssert.Contains(output, "\"measured\": 0");
        StringAssert.Contains(output, "\"unmeasured\": 1");
        StringAssert.Contains(output, "\"selector\": \"uniform\"");
        StringAssert.Contains(output, "unsuitable for reliable analysis");
    }

    [TestMethod]
    public async Task Audit_ContrastWithNoEligibleTextCandidates_IsComplete()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Window", Name = "App", IsEnabled = true },
            new UiElement { Id = "e1", Type = "Image", Name = "Logo", Width = 100, Height = 30 },
        ];
        _fakeUia.WindowCaptureException = new InvalidOperationException("capture should not be needed");

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--area", "contrast"]);
        var jsonExitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--area", "contrast", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, jsonExitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "Contrast coverage: no eligible visible text candidates.");
        StringAssert.Contains(TestAnsiConsole.Output, "\"attempted\": 0");
        StringAssert.Contains(TestAnsiConsole.Output, "\"measured\": 0");
        StringAssert.Contains(TestAnsiConsole.Output, "\"unmeasured\": 0");
    }

    [TestMethod]
    public async Task Audit_AreaNames_RunsOnlyNamesArea()
    {
        // Element fails names (no name) AND keyboard (interactive, not focusable), but we scope to
        // the names area only, so only a names finding should surface.
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e1", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = false, Selector = "btn-x" },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "names", "--json"]);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"ruleId\": \"names\"");
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"ruleId\":\\s*\"keyboard\""));
    }

    [TestMethod]
    public async Task Audit_LevelBasic_OmitsTabOrderFromKeyboardArea()
    {
        // Backward-jumping focusable elements: tab-order warns only under the thorough level.
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Button", Name = "First",  IsEnabled = true, IsKeyboardFocusable = true, X = 10, Y = 100 },
            new UiElement { Id = "e1", Type = "Button", Name = "Second", IsEnabled = true, IsKeyboardFocusable = true, X = 10, Y = 10 },
        ];

        var basicCmd = GetRequiredService<UiAuditCommand>();
        await ParseAndInvokeWithCaptureAsync(basicCmd, ["-a", "TestApp", "--area", "keyboard", "--level", "basic", "--json"]);
        StringAssert.DoesNotMatch(TestAnsiConsole.Output,
            new System.Text.RegularExpressions.Regex("\"ruleId\":\\s*\"tab-order\""));
    }

    [TestMethod]
    public async Task Audit_LevelThorough_ReportsTabOrder()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Button", Name = "First",  IsEnabled = true, IsKeyboardFocusable = true, X = 10, Y = 100 },
            new UiElement { Id = "e1", Type = "Button", Name = "Second", IsEnabled = true, IsKeyboardFocusable = true, X = 10, Y = 10 },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "keyboard", "--level", "thorough", "--json"]);
        StringAssert.Contains(TestAnsiConsole.Output, "\"ruleId\": \"tab-order\"");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Audit_InvalidArea_ReturnsError()
    {
        var originalError = Console.Error;
        using var jsonError = new StringWriter();
        try
        {
            Console.SetError(jsonError);
            var command = GetRequiredService<UiAuditCommand>();
            var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "bogus", "--json"]);
            Assert.AreEqual(1, exitCode);
            StringAssert.Contains(jsonError.ToString(), "\"code\": \"invalid_arguments\"");
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [TestMethod]
    public async Task Audit_LevelAliases_AreAccepted()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Window", Name = "App", IsEnabled = true },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var aaExit = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "names", "--level", "aa", "--json"]);
        var aaaExit = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "names", "--level", "aaa", "--json"]);

        Assert.AreEqual(0, aaExit);
        Assert.AreEqual(0, aaaExit);
    }

    [TestMethod]
    public async Task Audit_ZeroElements_FailsInsteadOfReportingClean()
    {
        _fakeUia.InspectResult = [];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "names", "--json"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"ruleId\": \"audit\"");
        StringAssert.Contains(TestAnsiConsole.Output, "\"fail\": 1");
        StringAssert.Contains(TestAnsiConsole.Output, "No UI Automation elements were discovered");
    }

    [TestMethod]
    public async Task Audit_TraversalDiagnostics_FailAndAreDeterministicallyOrdered()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Window", Name = "App", IsEnabled = true },
        ];
        _fakeUia.InspectIssues =
        [
            new UiInspectionIssue
            {
                Code = UiInspectionIssueCodes.SiblingEnumeration,
                Message = "Sibling enumeration failed.",
                Selector = "z-selector",
            },
            new UiInspectionIssue
            {
                Code = UiInspectionIssueCodes.ChildEnumeration,
                Message = "Child enumeration failed.",
                Selector = "a-selector",
            },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--area", "names", "--json"]);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"fail\": 2");
        var childIndex = output.IndexOf("Child enumeration failed.", StringComparison.Ordinal);
        var siblingIndex = output.IndexOf("Sibling enumeration failed.", StringComparison.Ordinal);
        Assert.IsTrue(childIndex >= 0 && siblingIndex > childIndex);
        Assert.AreEqual(UiAuditCommand.AuditMaxElements, _fakeUia.LastInspectOptions!.MaxElements);
        Assert.AreEqual(UiAuditCommand.AuditMaxTraversalDuration, _fakeUia.LastInspectOptions.MaxDuration);
        Assert.IsFalse(_fakeUia.LastInspectOptions.PromoteUniqueAutomationIds);
        Assert.IsTrue(_fakeUia.LastInspectOptions.IncludeScopedAncestorContext);
    }

    [TestMethod]
    public async Task Audit_Contrast_ButtonProviderText_IsNotFalselyClean()
    {
        _fakeSession.SessionResult = new UiSessionInfo
        {
            ProcessId = 1234,
            ProcessName = "TestApp",
            WindowTitle = "Test Window",
            WindowHandle = 100,
        };

        const int w = 20, h = 20;
        var buf = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var value = i < 100 ? (byte)0x99 : (byte)0xFF;
            buf[i * 4 + 0] = value;
            buf[i * 4 + 1] = value;
            buf[i * 4 + 2] = value;
            buf[i * 4 + 3] = 255;
        }
        _fakeUia.WindowCaptureResult = (buf, w, h, 0, 0);
        _fakeUia.InspectResult =
        [
            new UiElement
            {
                Id = "e0", Type = "Button", Name = "Save", Width = w, Height = h,
                WindowHandle = 100, Selector = "save-button",
            },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--area", "contrast", "--json"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "\"attempted\": 1");
        StringAssert.Contains(TestAnsiConsole.Output, "\"selector\": \"save-button\"");
        StringAssert.Contains(TestAnsiConsole.Output, "\"fail\": 1");
    }

    [TestMethod]
    public async Task Audit_Contrast_PartialCoverageFailsClosed()
    {
        _fakeSession.SessionResult = new UiSessionInfo
        {
            ProcessId = 1234,
            ProcessName = "TestApp",
            WindowTitle = "Test Window",
            WindowHandle = 100,
        };

        const int w = 20, h = 20;
        var buf = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var value = i < 100 ? (byte)0x00 : (byte)0xFF;
            buf[i * 4 + 0] = value;
            buf[i * 4 + 1] = value;
            buf[i * 4 + 2] = value;
            buf[i * 4 + 3] = 255;
        }
        _fakeUia.WindowCaptureResult = (buf, w, h, 0, 0);
        _fakeUia.InspectResult =
        [
            new UiElement
            {
                Id = "e0", Type = "Text", Name = "Measured", Width = w, Height = 16,
                WindowHandle = 100, Selector = "measured",
            },
            new UiElement
            {
                Id = "e1", Type = "Text", Name = "Popup", Width = w, Height = 16,
                WindowHandle = 200, Selector = "popup",
            },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--area", "contrast", "--json"]);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"pass\": 1");
        StringAssert.Contains(output, "\"fail\": 1");
        StringAssert.Contains(output, "\"attempted\": 2");
        StringAssert.Contains(output, "\"measured\": 1");
        StringAssert.Contains(output, "\"unmeasured\": 1");
        StringAssert.Contains(output, "\"selector\": \"popup\"");
    }

    [TestMethod]
    public async Task Audit_HumanOutput_ReportsIncompleteContrast()
    {
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Text", Name = "Hello", Width = 100, Height = 16 },
        ];
        _fakeUia.WindowCaptureException = new InvalidOperationException("no capture");

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "contrast"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "contrast was not measured because window capture");
        StringAssert.Contains(TestAnsiConsole.Output, "or bounded pixel analysis did not complete.");
        StringAssert.Contains(TestAnsiConsole.Output, "Contrast coverage: 1 attempted, 0 measured, 1 unmeasured.");
        StringAssert.Contains(TestAnsiConsole.Output, "0 checks passed");
    }

    [TestMethod]
    public async Task Audit_HumanOutput_EscapesSpectreMarkup()
    {
        _fakeUia.InspectResult =
        [
            new UiElement
            {
                Id = "e0", Type = "Button", Name = null, IsEnabled = true,
                IsKeyboardFocusable = true, Selector = "[btn-x]",
            },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "names"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, "[btn-x]");
        StringAssert.Contains(TestAnsiConsole.Output, "Summary:");
    }

    [TestMethod]
    public async Task Audit_AllAreasBasic_DeDuplicatesMissingNameAcrossAreas()
    {
        // No --area/--level: defaults to all areas, basic level. A single unnamed focusable
        // element trips the SAME missing-name defect in names, keyboard, and screen-reader. Cross-
        // area de-duplication collapses it to ONE failure (the canonical names finding) rather than
        // triple-counting it.
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Window", Name = "App", IsEnabled = true },
            new UiElement { Id = "e1", Type = "Button", Name = null, IsEnabled = true, IsKeyboardFocusable = true, Selector = "btn-x" },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"ruleId\": \"names\"");
        StringAssert.Contains(output, "\"fail\": 1");
        // The duplicate missing-name findings from keyboard/screen-reader are collapsed away.
        StringAssert.DoesNotMatch(output, new System.Text.RegularExpressions.Regex("\"fail\":\\s*3"));
    }

    [TestMethod]
    public async Task Audit_Contrast_HostedNativeHwnd_IsNotMeasuredAgainstRoot()
    {
        // Both elements came from the root inspection (source HWND 100), but the provider reports
        // that one is hosted by HWND 200. It must be unmeasured rather than sampled against the
        // root buffer. The in-window element IS scored (and fails).
        _fakeSession.SessionResult = new UiSessionInfo
        {
            ProcessId = 1234,
            ProcessName = "TestApp",
            WindowTitle = "Test Window",
            WindowHandle = 100,
        };

        // 20x20 grey (#999) glyphs on white → a clear sub-AA (~2.8:1) failure for normal text.
        const int w = 20, h = 20;
        var buf = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            byte v = i < 100 ? (byte)0x99 : (byte)0xFF; // 25% grey glyph coverage, rest white
            buf[i * 4 + 0] = v;
            buf[i * 4 + 1] = v;
            buf[i * 4 + 2] = v;
            buf[i * 4 + 3] = 255;
        }
        _fakeUia.WindowCaptureResult = (buf, w, h, 0, 0);

        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Text", Name = "InWin", Width = w, Height = 16, X = 0, Y = 0, WindowHandle = 100, NativeWindowHandle = 100, Selector = "in-win" },
            new UiElement { Id = "e1", Type = "Text", Name = "Hosted", Width = w, Height = 16, X = 0, Y = 0, WindowHandle = 100, NativeWindowHandle = 200, Selector = "hosted" },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "contrast", "--json"]);

        var output = TestAnsiConsole.Output;
        Assert.AreEqual(1, exitCode, "both low contrast and an unmeasured candidate should fail");
        StringAssert.Contains(output, "\"fail\": 2");
        StringAssert.Contains(output, "\"warn\": 0");
        StringAssert.Contains(output, "\"selector\": \"in-win\"");
        StringAssert.Contains(output, "\"selector\": \"hosted\"");
        StringAssert.Contains(output, "\"attempted\": 2");
        StringAssert.Contains(output, "\"measured\": 1");
        StringAssert.Contains(output, "\"unmeasured\": 1");
    }

    // A 20x20 buffer whose glyph pixels are mid-grey (#696969, ~5.5:1 on white) — above the AA
    // normal-text threshold (4.5) but below AAA (7.0).
    private static (byte[] Pixels, int W, int H, int OX, int OY) MidGreyOnWhiteCapture()
    {
        const int w = 20, h = 20;
        var buf = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            byte v = i < 100 ? (byte)0x69 : (byte)0xFF; // 25% grey glyph coverage, rest white
            buf[i * 4 + 0] = v;
            buf[i * 4 + 1] = v;
            buf[i * 4 + 2] = v;
            buf[i * 4 + 3] = 255;
        }
        return (buf, w, h, 0, 0);
    }

    [TestMethod]
    public async Task Audit_ContrastLevelBasic_MidGreyPassesUnderAa()
    {
        _fakeSession.SessionResult = new UiSessionInfo
        {
            ProcessId = 1234, ProcessName = "TestApp", WindowTitle = "Test Window", WindowHandle = 100,
        };
        _fakeUia.WindowCaptureResult = MidGreyOnWhiteCapture();
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Text", Name = "Grey", Width = 20, Height = 16, X = 0, Y = 0, WindowHandle = 100, Selector = "grey" },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "contrast", "--json"]);

        Assert.AreEqual(0, exitCode, "mid-grey (~5.5:1) meets the AA 4.5 threshold used by --level basic");
        StringAssert.Contains(TestAnsiConsole.Output, "\"fail\": 0");
    }

    [TestMethod]
    public async Task Audit_ContrastLevelThorough_UsesAaaThreshold()
    {
        _fakeSession.SessionResult = new UiSessionInfo
        {
            ProcessId = 1234, ProcessName = "TestApp", WindowTitle = "Test Window", WindowHandle = 100,
        };
        _fakeUia.WindowCaptureResult = MidGreyOnWhiteCapture();
        _fakeUia.InspectResult =
        [
            new UiElement { Id = "e0", Type = "Text", Name = "Grey", Width = 20, Height = 16, X = 0, Y = 0, WindowHandle = 100, Selector = "grey" },
        ];

        var command = GetRequiredService<UiAuditCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--area", "contrast", "--level", "thorough", "--json"]);

        Assert.AreEqual(1, exitCode, "mid-grey (~5.5:1) fails the AAA 7.0 threshold used by --level thorough");
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "\"fail\": 1");
        StringAssert.Contains(output, "AAA");
        StringAssert.Contains(output, "7.0:1");
    }
}
