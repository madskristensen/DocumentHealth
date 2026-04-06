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
        Assert.AreEqual(".md", options.IgnoredFileExtensions);
    }

    [TestMethod]
    public void IsFileExtensionIgnored_MatchingExtension_ReturnsTrue()
    {
        var options = new General { IgnoredFileExtensions = ".md, .txt" };

        Assert.IsTrue(options.IsFileExtensionIgnored("readme.md"));
        Assert.IsTrue(options.IsFileExtensionIgnored("notes.txt"));
    }

    [TestMethod]
    public void IsFileExtensionIgnored_NonMatchingExtension_ReturnsFalse()
    {
        var options = new General { IgnoredFileExtensions = ".md" };

        Assert.IsFalse(options.IsFileExtensionIgnored("program.cs"));
    }

    [TestMethod]
    public void IsFileExtensionIgnored_CaseInsensitive_ReturnsTrue()
    {
        var options = new General { IgnoredFileExtensions = ".MD" };

        Assert.IsTrue(options.IsFileExtensionIgnored("readme.md"));
    }

    [TestMethod]
    public void IsFileExtensionIgnored_WithoutDotPrefix_ReturnsTrue()
    {
        var options = new General { IgnoredFileExtensions = "md" };

        Assert.IsTrue(options.IsFileExtensionIgnored("readme.md"));
    }

    [TestMethod]
    public void IsFileExtensionIgnored_EmptyExtensions_ReturnsFalse()
    {
        var options = new General { IgnoredFileExtensions = "" };

        Assert.IsFalse(options.IsFileExtensionIgnored("readme.md"));
    }

    [TestMethod]
    public void IsFileExtensionIgnored_NullFilePath_ReturnsFalse()
    {
        var options = new General { IgnoredFileExtensions = ".md" };

        Assert.IsFalse(options.IsFileExtensionIgnored(null));
    }

    [TestMethod]
    public void IsFileExtensionIgnored_FileWithoutExtension_ReturnsFalse()
    {
        var options = new General { IgnoredFileExtensions = ".md" };

        Assert.IsFalse(options.IsFileExtensionIgnored("Makefile"));
    }
}
