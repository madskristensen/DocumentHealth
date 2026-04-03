using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DocumentHealth.Test;

[TestClass]
public class DiagnosticDataProviderTests
{
    [TestMethod]
    public void ExtractDiagnosticCode_ValidCSharpError_ReturnsCode()
    {
        string message = "CS0168: The variable 'x' is declared but never used";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.AreEqual("CS0168", result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_ValidAnalyzerError_ReturnsCode()
    {
        string message = "CA1000: Do not declare static members on generic types";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.AreEqual("CA1000", result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_FourLetterCode_ReturnsCode()
    {
        string message = "RULE1234: Some custom analyzer rule";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.AreEqual("RULE1234", result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_FiveDigitCode_ReturnsCode()
    {
        string message = "IDE12345: Use pattern matching";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.AreEqual("IDE12345", result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_NoCode_ReturnsNull()
    {
        string message = "This is just a message without a code";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_NullMessage_ReturnsNull()
    {
        string result = DiagnosticDataProvider.ExtractDiagnosticCode(null);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_EmptyMessage_ReturnsNull()
    {
        string result = DiagnosticDataProvider.ExtractDiagnosticCode("");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_WhitespaceMessage_ReturnsNull()
    {
        string result = DiagnosticDataProvider.ExtractDiagnosticCode("   ");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_CodeNotAtStart_ReturnsNull()
    {
        string message = "Error: CS0168 is the code";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void StripCodePrefix_ValidCodePrefix_RemovesCodeAndColon()
    {
        string message = "CS0168: The variable 'x' is declared but never used";
        string code = "CS0168";

        string result = DiagnosticDataProvider.StripCodePrefix(message, code);

        Assert.AreEqual("The variable 'x' is declared but never used", result);
    }

    [TestMethod]
    public void StripCodePrefix_CodeWithExtraSpaces_RemovesCodeAndSpaces()
    {
        string message = "CA1000:  Do not declare static members";
        string code = "CA1000";

        string result = DiagnosticDataProvider.StripCodePrefix(message, code);

        Assert.AreEqual("Do not declare static members", result);
    }

    [TestMethod]
    public void StripCodePrefix_CodeNotAtStart_ReturnsOriginalMessage()
    {
        string message = "Error CS0168: Some message";
        string code = "CS0168";

        string result = DiagnosticDataProvider.StripCodePrefix(message, code);

        Assert.AreEqual("Error CS0168: Some message", result);
    }

    [TestMethod]
    public void StripCodePrefix_NullMessage_ReturnsNull()
    {
        string result = DiagnosticDataProvider.StripCodePrefix(null, "CS0168");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void StripCodePrefix_NullCode_ReturnsOriginalMessage()
    {
        string message = "Some message";

        string result = DiagnosticDataProvider.StripCodePrefix(message, null);

        Assert.AreEqual(message, result);
    }

    [TestMethod]
    public void StripCodePrefix_EmptyCode_ReturnsOriginalMessage()
    {
        string message = "Some message";

        string result = DiagnosticDataProvider.StripCodePrefix(message, "");

        Assert.AreEqual(message, result);
    }

    [DataTestMethod]
    [DataRow("CS0168: Message", "CS0168", "Message")]
    [DataRow("CA1000: Message", "CA1000", "Message")]
    [DataRow("IDE0001:Message", "IDE0001", "Message")]
    [DataRow("RULE1234:   Message with spaces", "RULE1234", "Message with spaces")]
    public void StripCodePrefix_VariousFormats_StripsCorrectly(string message, string code, string expected)
    {
        string result = DiagnosticDataProvider.StripCodePrefix(message, code);

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void ExtractAndStripCode_RoundTrip_WorksTogether()
    {
        string originalMessage = "CS0246: The type or namespace name 'Foo' could not be found";

        string extractedCode = DiagnosticDataProvider.ExtractDiagnosticCode(originalMessage);
        string strippedMessage = DiagnosticDataProvider.StripCodePrefix(originalMessage, extractedCode);

        Assert.AreEqual("CS0246", extractedCode);
        Assert.AreEqual("The type or namespace name 'Foo' could not be found", strippedMessage);
    }
}
