using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using Microsoft.VisualStudio.Utilities;
using RenderedDocComments.Options;
using RenderedDocComments.Parsing;
using RenderedDocComments.Rendering;
using RenderedDocComments.Diagnostics;

namespace RenderedDocComments.Editor
{
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(DocCommentToggleMargin.MarginName)]
    [Order(After = PredefinedMarginNames.LineNumber, Before = PredefinedMarginNames.Outlining)]
    [MarginContainer(PredefinedMarginNames.Left)]
    [ContentType("CSharp")]
    [TextViewRole(Roles.Document)]
    [TextViewRole(Roles.Peek)]
    [TextViewRole(Roles.LeftDiff)]
    [TextViewRole(Roles.RightDiff)]
    internal sealed class DocCommentToggleMarginProvider : IWpfTextViewMarginProvider
    {
        [Import]
        internal EditorServices Services { get; set; }

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            var view = wpfTextViewHost.TextView;
            return DiffAwareness.IsInlineDiff(view)
                ? null
                : new DocCommentToggleMargin(view, Services.OutliningManagerService.GetOutliningManager(view));
        }
    }

    /// <summary>
    /// IntelliJ-style gutter icon on the first line of every doc comment: a pencil on a card
    /// (click to edit the raw text) and a book on a raw comment (click to render it). Works for
    /// single-line comments too, which have no outlining arrow.
    /// </summary>
    internal sealed class DocCommentToggleMargin : Canvas, IWpfTextViewMargin
    {
        public const string MarginName = "RenderedDocComments.ToggleMargin";

        private const double IconSize = 16;
        private const string EditGlyph = "";   // Segoe Fluent Icons: Edit
        private const string RenderGlyph = ""; // Segoe Fluent Icons: ReadingMode

        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        private readonly IWpfTextView _view;
        private readonly IOutliningManager _outlining;
        private readonly DocCommentBufferState _state;
        private readonly List<Border> _icons = new List<Border>();
        private bool _redrawScheduled;
        private bool _disposed;

        public DocCommentToggleMargin(IWpfTextView view, IOutliningManager outlining)
        {
            _view = view;
            _outlining = outlining;
            _state = DocCommentBufferState.Get(view.TextBuffer);

            ExtensionOptions.EnsureLoaded();
            Width = ExtensionOptions.ShowGutterIcon ? IconSize + 2 : 0;
            ClipToBounds = true;
            Background = Brushes.Transparent;

            // VS zooms its own margins; custom ones have to follow the zoom level themselves,
            // otherwise icons drift away from their lines the further down the file they are.
            double zoom = _view.ZoomLevel / 100.0;
            LayoutTransform = new ScaleTransform(zoom, zoom);
            _view.ZoomLevelChanged += OnZoomLevelChanged;

            _view.LayoutChanged += OnLayoutChanged;
            _state.Changed += OnChanged;
            DocTheme.Changed += OnChanged;
            ExtensionOptions.Changed += OnOptionsChanged;
        }

        private void OnOptionsChanged(object sender, EventArgs e)
        {
            Width = ExtensionOptions.ShowGutterIcon ? IconSize + 2 : 0;
            OnChanged(sender, e);
        }

        public FrameworkElement VisualElement => this;

        public double MarginSize => ActualWidth;

        public bool Enabled => true;

        public ITextViewMargin GetTextViewMargin(string marginName) =>
            string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _view.ZoomLevelChanged -= OnZoomLevelChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _state.Changed -= OnChanged;
            DocTheme.Changed -= OnChanged;
            ExtensionOptions.Changed -= OnOptionsChanged;
        }

        private void OnZoomLevelChanged(object sender, ZoomLevelChangedEventArgs e) => LayoutTransform = e.ZoomTransform;

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Guard.Run("Drawing gutter icons", Redraw);

        private void OnChanged(object sender, EventArgs e)
        {
            if (_redrawScheduled || _disposed) return;
            _redrawScheduled = true;
            Guard.Later(Dispatcher, DispatcherPriority.Normal, "Drawing gutter icons", new Action(() =>
            {
                _redrawScheduled = false;
                Redraw();
            }));
        }

        /// <summary>
        /// Place an icon on the first line of each visible comment. The icons are kept and
        /// updated from one layout to the next (layout happens on every keystroke); spare ones
        /// are hidden rather than removed.
        /// </summary>
        private void Redraw()
        {
            int used = 0;
            try
            {
                if (_disposed || !ExtensionOptions.ShowGutterIcon || _view.IsClosed || _view.InLayout || _view.TextViewLines == null)
                {
                    return;
                }

                var lines = _view.TextViewLines;
                var formatted = lines.FormattedSpan;
                var snapshot = formatted.Snapshot;
                if (lines.Count == 0 || snapshot != _view.TextSnapshot || DocCommentScanner.GetBlocks(snapshot).Count == 0)
                {
                    return;
                }

                // Only the comments on screen, with the collapsed folds read once (this runs on
                // every layout, including every step of a scroll).
                var theme = DocTheme.Current;
                var folds = CollapsedFolds.Read(_outlining, formatted);
                int firstLine = formatted.Start.GetContainingLine().LineNumber;
                int lastLine = formatted.End.GetContainingLine().LineNumber;
                foreach (var block in DocCommentScanner.BlocksInLines(snapshot, firstLine, lastLine))
                {
                    if (block.StartLine < firstLine) continue;
                    var line = lines.GetTextViewLineContainingBufferPosition(block.Span.Start);
                    if (line == null) continue;

                    bool hidden = folds != null && folds.Covers(block)
                        ? folds.HidesFirstLine(block)
                        : DocCardVisibility.IsHiddenByOtherFold(_outlining, block);
                    if (hidden) continue; // Folded away by VS.
                    if (DiffAwareness.IsChangedInDiff(_view, block)) continue; // Always raw in a diff: nothing to toggle.

                    bool showingCard = DocCardVisibility.IsShowingCard(_view, _state, _outlining, block, folds);
                    var icon = IconAt(used++);
                    Show(icon, block, showingCard, theme);
                    SetLeft(icon, 1);
                    SetTop(icon, line.TextTop - _view.ViewportTop + Math.Max(0, (line.TextHeight - IconSize) / 2));
                }
            }
            finally
            {
                for (int i = used; i < _icons.Count; i++)
                {
                    _icons[i].Visibility = Visibility.Collapsed;
                }
            }
        }

        private Border IconAt(int index)
        {
            if (index < _icons.Count)
            {
                _icons[index].Visibility = Visibility.Visible;
                return _icons[index];
            }

            var icon = CreateIcon();
            _icons.Add(icon);
            Children.Add(icon);
            return icon;
        }

        private static void Show(Border icon, DocCommentBlock block, bool showingCard, DocTheme theme)
        {
            var glyph = (TextBlock)icon.Child;
            glyph.Text = showingCard ? EditGlyph : RenderGlyph;
            glyph.Foreground = icon.IsMouseOver ? theme.PlainText : theme.Muted;
            icon.Background = icon.IsMouseOver ? theme.InlineCodeBackground : Brushes.Transparent;

            string tip = showingCard ? "Edit comment (Ctrl+M, Ctrl+Q)" : "Show rendered comment (Ctrl+M, Ctrl+Q)";
            if (!Equals(icon.ToolTip, tip)) icon.ToolTip = tip;

            icon.Tag = block.Span; // The comment this icon switches (at this snapshot).
        }

        private Border CreateIcon()
        {
            var glyph = new TextBlock
            {
                FontFamily = IconFont,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var icon = new Border
            {
                Width = IconSize,
                Height = IconSize,
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                Child = glyph,
                Cursor = Cursors.Hand,
            };

            icon.MouseEnter += (s, e) =>
            {
                icon.Background = DocTheme.Current.InlineCodeBackground;
                glyph.Foreground = DocTheme.Current.PlainText;
            };
            icon.MouseLeave += (s, e) =>
            {
                icon.Background = Brushes.Transparent;
                glyph.Foreground = DocTheme.Current.Muted;
            };
            icon.MouseLeftButtonDown += (s, e) => e.Handled = true;
            icon.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                Guard.Run("Switching a comment", () =>
                {
                    if (!(icon.Tag is SnapshotSpan comment)) return;
                    var current = _state.FindBlock(comment.Snapshot.CreateTrackingSpan(comment, SpanTrackingMode.EdgeInclusive));
                    if (current != null)
                    {
                        DocCommentViewController.For(_view)?.ToggleBlock(current);
                    }
                });
            };

            return icon;
        }
    }
}
