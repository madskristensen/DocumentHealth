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

    #region Regex Boundary Tests - Letters

    [TestMethod]
    public void ExtractDiagnosticCode_TwoLetterCode_ReturnsCode()
    {
        // Regex requires 2-4 letters - 2 is minimum valid
        string message = "CS1234: Some message";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.AreEqual("CS1234", result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_SingleLetterCode_ReturnsNull()
    {
        // Regex requires 2-4 letters - 1 letter should fail
        string message = "A1234: Some message";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_FiveLetterCode_ReturnsNull()
    {
        // Regex requires 2-4 letters - 5 letters should fail
        string message = "ABCDE1234: Some message";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_LowercaseCode_ReturnsNull()
    {
        // Regex requires uppercase letters
        string message = "cs0168: Some message";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.IsNull(result);
    }

    #endregion

    #region Regex Boundary Tests - Digits

    [TestMethod]
    public void ExtractDiagnosticCode_FourDigitCode_ReturnsCode()
    {
        // Regex requires 4-5 digits - 4 is minimum valid
        string message = "CS1234: Some message";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.AreEqual("CS1234", result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_ThreeDigitCode_ReturnsNull()
    {
        // Regex requires 4-5 digits - 3 digits should fail
        string message = "CS123: Some message";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_SixDigitCode_ReturnsNull()
    {
        // Regex requires 4-5 digits - 6 digits should fail
        string message = "CS123456: Some message";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.IsNull(result);
    }

    #endregion

    #region Regex Format Tests

    [TestMethod]
    public void ExtractDiagnosticCode_MissingColon_ReturnsNull()
    {
        // Regex requires colon after code
        string message = "CS0168 Some message without colon";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ExtractDiagnosticCode_SpaceBeforeColon_ReturnsCode()
    {
        // Regex allows optional whitespace before colon
        string message = "CS0168 : Some message";

        string result = DiagnosticDataProvider.ExtractDiagnosticCode(message);

        Assert.AreEqual("CS0168", result);
    }

    #endregion

    #region StripCodePrefix Edge Cases

    [TestMethod]
    public void StripCodePrefix_CodeWithoutColon_StripsCodeOnly()
    {
        // When code doesn't have colon, just strips the code
        string message = "CS0168 Some message";
        string code = "CS0168";

        string result = DiagnosticDataProvider.StripCodePrefix(message, code);

        Assert.AreEqual("Some message", result);
    }

    #endregion
}

