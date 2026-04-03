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
