// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class MsBuildPropertyReaderTests
{
    private static readonly string[] MultipleNames = ["TargetDir", "RunCommand", "WindowsPackageType"];
    private static readonly string[] SingleName = ["OutputType"];

    [TestMethod]
    public void Parse_MultipleProperties_ParsesJsonObject()
    {
        var stdout = """
            {
              "Properties": {
                "TargetDir": "C:\\repo\\bin\\x64\\Debug\\net10.0\\win-x64\\",
                "RunCommand": "C:\\repo\\bin\\x64\\Debug\\net10.0\\win-x64\\App.exe",
                "WindowsPackageType": "MSIX"
              }
            }
            """;

        var result = MsBuildPropertyReader.Parse(stdout, MultipleNames);

        Assert.AreEqual(@"C:\repo\bin\x64\Debug\net10.0\win-x64\", result["TargetDir"]);
        Assert.AreEqual(@"C:\repo\bin\x64\Debug\net10.0\win-x64\App.exe", result["RunCommand"]);
        Assert.AreEqual("MSIX", result["WindowsPackageType"]);
    }

    [TestMethod]
    public void Parse_LookupIsCaseInsensitive()
    {
        var stdout = """{ "Properties": { "TargetDir": "X" } }""";

        var result = MsBuildPropertyReader.Parse(stdout, MultipleNames);

        Assert.AreEqual("X", result["targetdir"]);
        Assert.AreEqual("X", result["TARGETDIR"]);
    }

    [TestMethod]
    public void Parse_JsonWithLeadingDiagnosticPreamble_StillParses()
    {
        // Tolerate a stray line before the JSON object (defensive; normally stdout is clean JSON).
        var stdout = "some warning text\n{ \"Properties\": { \"WindowsPackageType\": \"None\" } }";

        var result = MsBuildPropertyReader.Parse(stdout, MultipleNames);

        Assert.AreEqual("None", result["WindowsPackageType"]);
    }

    [TestMethod]
    public void Parse_EmptyStringPropertyValue_Preserved()
    {
        var stdout = """{ "Properties": { "WindowsPackageType": "" } }""";

        var result = MsBuildPropertyReader.Parse(stdout, MultipleNames);

        Assert.IsTrue(result.ContainsKey("WindowsPackageType"));
        Assert.AreEqual(string.Empty, result["WindowsPackageType"]);
    }

    [TestMethod]
    public void Parse_SingleProperty_ScalarOutput_ReturnsWholeValue()
    {
        var result = MsBuildPropertyReader.Parse("WinExe\n", SingleName);

        Assert.AreEqual("WinExe", result["OutputType"]);
    }

    [TestMethod]
    public void Parse_MultipleRequested_ButScalarOutput_ReturnsEmpty()
    {
        // A non-JSON scalar is only meaningful when exactly one property was requested.
        var result = MsBuildPropertyReader.Parse("not-json", MultipleNames);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Parse_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.AreEqual(0, MsBuildPropertyReader.Parse("", SingleName).Count);
        Assert.AreEqual(0, MsBuildPropertyReader.Parse("   \r\n  ", SingleName).Count);
    }

    [TestMethod]
    public void Parse_MissingPropertiesObject_FallsBackToScalarForSingle()
    {
        // JSON that is not a { "Properties": {...} } wrapper is treated as a scalar for a single request.
        var result = MsBuildPropertyReader.Parse("[1,2,3]", SingleName);

        Assert.AreEqual("[1,2,3]", result["OutputType"]);
    }
}
