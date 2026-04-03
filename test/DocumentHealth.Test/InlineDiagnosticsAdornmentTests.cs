namespace DocumentHealth.Test;

[TestClass]
public class InlineDiagnosticsAdornmentTests
{
    [TestMethod]
    public void FormatMessage_DefaultTemplate_ReturnsMessageOnly()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "{message}",
            "Variable 'x' is never used",
            "CS0168",
            DiagnosticSeverity.Warning,
            "Compiler");

        Assert.AreEqual("Variable 'x' is never used", result);
    }

    [TestMethod]
    public void FormatMessage_CodeTemplate_ReturnsCode()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "{code}",
            "Variable 'x' is never used",
            "CS0168",
            DiagnosticSeverity.Warning,
            "Compiler");

        Assert.AreEqual("CS0168", result);
    }

    [TestMethod]
    public void FormatMessage_SeverityTemplate_ReturnsSeverity()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "{severity}",
            "Some message",
            "CS0001",
            DiagnosticSeverity.Error,
            "Compiler");

        Assert.AreEqual("Error", result);
    }

    [TestMethod]
    public void FormatMessage_SourceTemplate_ReturnsSource()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "{source}",
            "Some message",
            "CA1000",
            DiagnosticSeverity.Warning,
            "FxCop");

        Assert.AreEqual("FxCop", result);
    }

    [TestMethod]
    public void FormatMessage_ComplexTemplate_ReplacesAllPlaceholders()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "[{severity}] {code}: {message} (from {source})",
            "Variable 'x' is never used",
            "CS0168",
            DiagnosticSeverity.Warning,
            "Compiler");

        Assert.AreEqual("[Warning] CS0168: Variable 'x' is never used (from Compiler)", result);
    }

    [TestMethod]
    public void FormatMessage_ErrorSeverity_ReturnsError()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "{severity}",
            "Some error",
            "CS0001",
            DiagnosticSeverity.Error,
            null);

        Assert.AreEqual("Error", result);
    }

    [TestMethod]
    public void FormatMessage_WarningSeverity_ReturnsWarning()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "{severity}",
            "Some warning",
            "CS0168",
            DiagnosticSeverity.Warning,
            null);

        Assert.AreEqual("Warning", result);
    }

    [TestMethod]
    public void FormatMessage_MessageSeverity_ReturnsInfo()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "{severity}",
            "Some info",
            "IDE0001",
            DiagnosticSeverity.Message,
            null);

        Assert.AreEqual("Info", result);
    }

    [TestMethod]
    public void FormatMessage_NullMessage_ReturnsEmptyString()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "{message}",
            null,
            "CS0168",
            DiagnosticSeverity.Warning,
            "Compiler");

        Assert.AreEqual("", result);
    }

    [TestMethod]
    public void FormatMessage_NullCode_ReturnsEmptyString()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "{code}",
            "Some message",
            null,
            DiagnosticSeverity.Warning,
            "Compiler");

        Assert.AreEqual("", result);
    }

    [TestMethod]
    public void FormatMessage_NullSource_ReturnsEmptyString()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "{source}",
            "Some message",
            "CS0168",
            DiagnosticSeverity.Warning,
            null);

        Assert.AreEqual("", result);
    }

    [TestMethod]
    public void FormatMessage_NullTemplate_UsesDefaultTemplate()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            null,
            "Variable 'x' is never used",
            "CS0168",
            DiagnosticSeverity.Warning,
            "Compiler");

        Assert.AreEqual("Variable 'x' is never used", result);
    }

    [TestMethod]
    [DataRow("{message}", "Test message", "Test message")]
    [DataRow("{code}: {message}", "Test message", "CS0001: Test message")]
    [DataRow("[{severity}] {message}", "Test message", "[Error] Test message")]
    [DataRow("{code} - {message} ({source})", "Test message", "CS0001 - Test message (Analyzer)")]
    public void FormatMessage_VariousTemplates_FormatsCorrectly(string template, string message, string expected)
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            template,
            message,
            "CS0001",
            DiagnosticSeverity.Error,
            "Analyzer");

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void FormatMessage_MultiplePlaceholdersOfSameType_ReplacesAll()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "{message} -- {message}",
            "Test",
            "CS0001",
            DiagnosticSeverity.Error,
            "Source");

        Assert.AreEqual("Test -- Test", result);
    }

    [TestMethod]
    public void FormatMessage_NoPlaceholders_ReturnsTemplateAsIs()
    {
        string result = InlineDiagnosticsAdornment.FormatMessage(
            "Static text without placeholders",
            "Message",
            "Code",
            DiagnosticSeverity.Error,
            "Source");

        Assert.AreEqual("Static text without placeholders", result);
    }
}
