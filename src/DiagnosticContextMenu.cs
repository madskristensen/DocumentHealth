using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

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
                Icon = new CrispImage { Moniker = KnownMonikers.Copy, Width = 16, Height = 16 },
            };
            copyMessage.Click += (s, e) =>
            {
                Clipboard.SetText(diagnostic.PrimaryMessage ?? "");
            };

            var copyCode = new MenuItem
            {
                Header = "Copy Diagnostic Code",
                Icon = new CrispImage { Moniker = KnownMonikers.CodeInformation, Width = 16, Height = 16 },
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
                Icon = new CrispImage { Moniker = KnownMonikers.SearchContract, Width = 16, Height = 16 },
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
                Icon = new CrispImage { Moniker = KnownMonikers.Settings, Width = 16, Height = 16 },
            };
            settings.Click += (s, e) => VS.Settings.OpenAsync<OptionsProvider.GeneralOptions>().FireAndForget();
            menu.Items.Add(settings);

            ThemedContextMenuHelper.ApplyVsTheme(menu);

            return menu;
        }
    }
}
