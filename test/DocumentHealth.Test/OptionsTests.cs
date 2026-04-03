using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DocumentHealth.Test;

[TestClass]
public class OptionsTests
{
    [TestMethod]
    public void General_DefaultValues_AreSetCorrectly()
    {
        var options = new General();

        Assert.AreEqual(250, options.UpdateDelayMilliseconds);
        Assert.IsTrue(options.ShowMessages);
        Assert.IsTrue(options.ReplaceBuiltInIndicator);
        Assert.AreEqual(UpdateMode.OnSave, options.UpdateMode);
        Assert.IsTrue(options.ShowInlineMessages);
        Assert.AreEqual(SeverityFilter.ErrorsAndWarnings, options.ShowGutterIcons);
        Assert.IsTrue(options.ShowErrors);
        Assert.IsTrue(options.ShowWarnings);
        Assert.IsFalse(options.ShowSuggestions);
        Assert.AreEqual(SeverityFilter.ErrorsAndWarnings, options.HighlightLines);
        Assert.AreEqual(MessagePosition.Inline, options.MessagePlacement);
        Assert.AreEqual("{message}", options.MessageTemplate);
    }

    [TestMethod]
    public void SeverityFilter_Enum_HasExpectedValues()
    {
        Assert.AreEqual(0, (int)SeverityFilter.None);
        Assert.AreEqual(1, (int)SeverityFilter.Errors);
        Assert.AreEqual(2, (int)SeverityFilter.ErrorsAndWarnings);
        Assert.AreEqual(3, (int)SeverityFilter.All);
    }

    [TestMethod]
    public void UpdateMode_Enum_HasExpectedValues()
    {
        Assert.AreEqual(0, (int)UpdateMode.Continuous);
        Assert.AreEqual(1, (int)UpdateMode.OnSave);
    }

    [TestMethod]
    public void MessagePosition_Enum_HasExpectedValues()
    {
        Assert.AreEqual(0, (int)MessagePosition.Inline);
        Assert.AreEqual(1, (int)MessagePosition.Above);
        Assert.AreEqual(2, (int)MessagePosition.Below);
    }
}
