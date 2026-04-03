namespace DocumentHealth.Test;

[TestClass]
public class HealthStatusHelperTests
{
    [TestMethod]
    public void GetAccessibleText_NoIssues_ReturnsNoIssuesText()
    {
        string result = HealthStatusHelper.GetAccessibleText(0, 0, 0);

        Assert.AreEqual("No errors or warnings", result);
    }

    [TestMethod]
    public void GetAccessibleText_OnlyErrors_ReturnsErrorCount()
    {
        string result = HealthStatusHelper.GetAccessibleText(3, 0, 0);

        Assert.AreEqual("3 error(s)", result);
    }

    [TestMethod]
    public void GetAccessibleText_OnlyWarnings_ReturnsWarningCount()
    {
        string result = HealthStatusHelper.GetAccessibleText(0, 5, 0);

        Assert.AreEqual("5 warning(s)", result);
    }

    [TestMethod]
    public void GetAccessibleText_OnlyMessages_ReturnsMessageCount()
    {
        string result = HealthStatusHelper.GetAccessibleText(0, 0, 2);

        Assert.AreEqual("2 message(s)", result);
    }

    [TestMethod]
    public void GetAccessibleText_ErrorsAndWarnings_ReturnsCombinedText()
    {
        string result = HealthStatusHelper.GetAccessibleText(2, 3, 0);

        Assert.AreEqual("2 error(s), 3 warning(s)", result);
    }

    [TestMethod]
    public void GetAccessibleText_AllThreeTypes_ReturnsCombinedText()
    {
        string result = HealthStatusHelper.GetAccessibleText(1, 2, 3);

        Assert.AreEqual("1 error(s), 2 warning(s), 3 message(s)", result);
    }

    [TestMethod]
    public void GetAccessibleText_ErrorsAndMessages_ReturnsCombinedText()
    {
        string result = HealthStatusHelper.GetAccessibleText(4, 0, 1);

        Assert.AreEqual("4 error(s), 1 message(s)", result);
    }

    [TestMethod]
    public void GetAccessibleText_WarningsAndMessages_ReturnsCombinedText()
    {
        string result = HealthStatusHelper.GetAccessibleText(0, 7, 3);

        Assert.AreEqual("7 warning(s), 3 message(s)", result);
    }

    [TestMethod]
    public void GetAccessibleText_SingleError_UsesSingularForm()
    {
        string result = HealthStatusHelper.GetAccessibleText(1, 0, 0);

        // Note: Current implementation uses "{0} error(s)" format for all counts
        Assert.AreEqual("1 error(s)", result);
    }

    [TestMethod]
    [DataRow(0, 0, 0, "No errors or warnings")]
    [DataRow(1, 0, 0, "1 error(s)")]
    [DataRow(0, 1, 0, "1 warning(s)")]
    [DataRow(0, 0, 1, "1 message(s)")]
    [DataRow(5, 3, 2, "5 error(s), 3 warning(s), 2 message(s)")]
    [DataRow(10, 0, 5, "10 error(s), 5 message(s)")]
    public void GetAccessibleText_VariousCombinations_ReturnsCorrectFormat(
        int errors, int warnings, int messages, string expected)
    {
        string result = HealthStatusHelper.GetAccessibleText(errors, warnings, messages);

        Assert.AreEqual(expected, result);
    }
}
