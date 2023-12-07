﻿using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;

namespace DocumentHealth
{
    internal class HealthMargin : DockPanel, IWpfTextViewMargin
    {
        private readonly string _fileName;
        private readonly IWpfTableControl _table;
        private readonly IWpfTextView _view;
        private bool _isDisposed;

        public HealthMargin(IWpfTextView textView, IErrorList errorList)
        {
            _fileName = textView.TextBuffer.GetFileName();
            _table = errorList.TableControl;
            _view = textView;

            _table.EntriesChanged += OnEntriesChanged;
            MouseUp += OnMouseUp;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ScrollBarBackgroundBrushKey);
        }

        private void OnMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            VS.Commands.ExecuteAsync("View.NextError").FireAndForget();
        }

        private void OnEntriesChanged(object sender, EntriesChangedEventArgs e)
        {
            UpdateAsync().FireAndForget();
        }

        public async Task UpdateAsync()
        {
            // Move to the background thread
            await TaskScheduler.Default;

            var errors = 0;
            var warnings = 0;

            foreach (ITableEntryHandle entry in _table.Entries)
            {
                if (!entry.TryGetValue(StandardTableKeyNames.DocumentName, out string fileName) || fileName != _fileName)
                {
                    continue;
                }

                if (!entry.TryGetValue(StandardTableKeyNames.ErrorSeverity, out __VSERRORCATEGORY severity))
                {
                    continue;
                }

                errors += severity == __VSERRORCATEGORY.EC_ERROR ? 1 : 0;
                warnings += severity == __VSERRORCATEGORY.EC_WARNING ? 1 : 0;
            }

            ImageMoniker moniker = KnownMonikers.StatusOK;
            if (errors > 0)
            {
                moniker = KnownMonikers.StatusError;
            }
            else if (warnings > 0)
            {
                moniker = KnownMonikers.StatusWarning;
            }

            // Move back to the UI thread
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var image = new CrispImage
            {
                Moniker = moniker,
                Width = 16,
                Height = 16,
                Margin = new Thickness(2),
                ToolTip = $"This file contains {errors} errors and {warnings} warnings.\r\nClick to go to the next instance (Ctrl+Shift+F12)"
            };

            Children.Clear();
            Children.Add(image);
        }

        public FrameworkElement VisualElement => this;

        public double MarginSize => ActualHeight;

        public bool Enabled => true;

        public ITextViewMargin GetTextViewMargin(string marginName)
        {
            return (marginName == nameof(HealthMargin)) ? this : null;
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                GC.SuppressFinalize(this);

                _table.EntriesChanged -= OnEntriesChanged;
                MouseUp -= OnMouseUp;
            }
        }
    }
}