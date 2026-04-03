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
}
