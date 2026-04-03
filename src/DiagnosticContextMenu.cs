using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;

namespace DocumentHealth
{
    /// <summary>
    /// Creates themed context menus for diagnostic indicators (inline messages, gutter icons).
    /// </summary>
    internal static class DiagnosticContextMenu
    {
        public static ContextMenu Create(LineDiagnostic diagnostic)
        {
            var menu = new ContextMenu();

            var copyMessage = new MenuItem
            {
                Header = "Copy Diagnostic Message",
                Icon = CreateThemedIcon(KnownMonikers.Copy),
            };
            copyMessage.Click += (s, e) =>
            {
                Clipboard.SetText(diagnostic.PrimaryMessage ?? "");
            };

            var copyCode = new MenuItem
            {
                Header = "Copy Diagnostic Code",
                Icon = CreateThemedIcon(KnownMonikers.CodeInformation),
                IsEnabled = !string.IsNullOrEmpty(diagnostic.DiagnosticCode),
            };
            copyCode.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(diagnostic.DiagnosticCode))
                {
                    Clipboard.SetText(diagnostic.DiagnosticCode);
                }
            };

            var searchOnline = new MenuItem
            {
                Header = "Search Online",
                Icon = CreateThemedIcon(KnownMonikers.SearchContract),
            };
            searchOnline.Click += (s, e) =>
            {
                string query = !string.IsNullOrEmpty(diagnostic.DiagnosticCode)
                    ? $"{diagnostic.DiagnosticCode} {diagnostic.PrimaryMessage}"
                    : diagnostic.PrimaryMessage ?? "";

                string url = "https://www.bing.com/search?q=" + System.Uri.EscapeDataString(query);

                var psi = new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                };
                Process.Start(psi);
            };

            menu.Items.Add(copyMessage);
            menu.Items.Add(copyCode);
            menu.Items.Add(new Separator());
            menu.Items.Add(searchOnline);
            menu.Items.Add(new Separator());
            AddDisplayToggleSubmenu(menu);
            menu.Items.Add(new Separator());

            var settings = new MenuItem
            {
                Header = "Settings...",
                Icon = CreateThemedIcon(KnownMonikers.Settings),
            };
            settings.Click += (s, e) => VS.Settings.OpenAsync<OptionsProvider.GeneralOptions>().FireAndForget();
            menu.Items.Add(settings);

            ThemedContextMenuHelper.ApplyVsTheme(menu);

            return menu;
        }

        /// <summary>
        /// Appends a "Display" submenu with checkable toggle items for key display options.
        /// </summary>
        public static void AddDisplayToggleSubmenu(ContextMenu menu)
        {
            var displayMenu = new MenuItem
            {
                Header = "Display",
                Icon = CreateThemedIcon(KnownMonikers.ShowAllCode),
            };

            var inlineMessages = CreateToggleItem("Inline Messages", () => General.Instance.ShowInlineMessages);
            inlineMessages.Click += (s, e) =>
            {
                General options = General.Instance;
                options.ShowInlineMessages = !options.ShowInlineMessages;
                options.Save();
            };

            var highlightLines = CreateToggleItem("Highlight Lines", () => General.Instance.HighlightLines != SeverityFilter.None);
            highlightLines.Click += (s, e) =>
            {
                General options = General.Instance;
                options.HighlightLines = options.HighlightLines != SeverityFilter.None
                    ? SeverityFilter.None
                    : SeverityFilter.ErrorsAndWarnings;
                options.Save();
            };

            var showErrors = CreateToggleItem("Errors", () => General.Instance.ShowErrors);
            showErrors.Click += (s, e) =>
            {
                General options = General.Instance;
                options.ShowErrors = !options.ShowErrors;
                options.Save();
            };

            var showWarnings = CreateToggleItem("Warnings", () => General.Instance.ShowWarnings);
            showWarnings.Click += (s, e) =>
            {
                General options = General.Instance;
                options.ShowWarnings = !options.ShowWarnings;
                options.Save();
            };

            var showSuggestions = CreateToggleItem("Suggestions", () => General.Instance.ShowSuggestions);
            showSuggestions.Click += (s, e) =>
            {
                General options = General.Instance;
                options.ShowSuggestions = !options.ShowSuggestions;
                options.Save();
            };

            var gutterIcons = CreateToggleItem("Gutter Icons", () => General.Instance.ShowGutterIcons != SeverityFilter.None);
            gutterIcons.Click += (s, e) =>
            {
                General options = General.Instance;
                options.ShowGutterIcons = options.ShowGutterIcons != SeverityFilter.None
                    ? SeverityFilter.None
                    : SeverityFilter.ErrorsAndWarnings;
                options.Save();
            };

            displayMenu.Items.Add(inlineMessages);
            displayMenu.Items.Add(highlightLines);
            displayMenu.Items.Add(new Separator());
            displayMenu.Items.Add(showErrors);
            displayMenu.Items.Add(showWarnings);
            displayMenu.Items.Add(showSuggestions);
            displayMenu.Items.Add(new Separator());
            displayMenu.Items.Add(gutterIcons);

            menu.Items.Add(displayMenu);
        }

        /// <summary>
        /// Creates a toggle menu item that shows a checkmark icon when the option is enabled.
        /// </summary>
        private static MenuItem CreateToggleItem(string header, Func<bool> isChecked)
        {
            CrispImage checkmark = CreateThemedIcon(KnownMonikers.Checkmark);

            var item = new MenuItem
            {
                Header = header,
                Icon = isChecked() ? checkmark : null,
            };

            // Refresh the checkmark icon each time the menu opens, since options may change externally.
            item.Loaded += (s, e) => item.Icon = isChecked() ? checkmark : null;

            return item;
        }

        private static CrispImage CreateThemedIcon(ImageMoniker moniker)
        {
            var image = new CrispImage
            {
                Moniker = moniker,
                Width = 16,
                Height = 16,
            };

            // Disable automatic background color detection to prevent the visual shift
            // when mouse hovers over menu items and the highlight background changes.
            // This tells CrispImage not to automatically re-render when background changes.
            ImageThemingUtilities.SetImageBackgroundColor(image, GetIconBackgroundColor());

            return image;
        }

        private static System.Windows.Media.Color GetIconBackgroundColor()
        {
            // Get the actual menu icon background color from VS theme
            var brush = (System.Windows.Media.SolidColorBrush)Application.Current.TryFindResource(
                Microsoft.VisualStudio.Shell.VsBrushes.CommandBarMenuIconBackgroundKey);

            return brush?.Color ?? System.Windows.Media.Colors.White;
        }
    }
}
