using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Outlining;
using Microsoft.VisualStudio.Utilities;
using RenderedDocComments.Navigation;
using RenderedDocComments.Parsing;
using RenderedDocComments.Rendering;
using RenderedDocComments.Diagnostics;

namespace RenderedDocComments.Editor
{
    [Export(typeof(ILineTransformSourceProvider))]
    [ContentType("CSharp")]
    [TextViewRole(Roles.Document)]
    [TextViewRole(Roles.Peek)]
    [TextViewRole(Roles.LeftDiff)]
    [TextViewRole(Roles.RightDiff)]
    internal sealed class DocCommentCardLayerProvider : ILineTransformSourceProvider
    {
        public const string LayerName = "RenderedDocComments.Cards";

#pragma warning disable 169, 649, IDE0044, IDE0051 // Read by MEF.
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(LayerName)]
        [Order(After = PredefinedAdornmentLayers.Caret)] // Above the caret: a caret on the hidden first line (mid-drag) isn't drawn over the card.
        private AdornmentLayerDefinition _layerDefinition;
#pragma warning restore 169, 649, IDE0044, IDE0051

        [Import]
        internal EditorServices Services { get; set; }

        public ILineTransformSource Create(IWpfTextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(
                () => new DocCommentCardLayer(textView, Services.OutliningManagerService.GetOutliningManager(textView)));
    }

    /// <summary>
    /// Draws the cards. The visual line holding a rendered comment's first line (plus the fold over
    /// lines 2..N) keeps its normal text height and gets the rest of the card's height as space
    /// below it. The card is drawn on its own layer above the text, over an editor-coloured strip
    /// that hides that line's raw text, the fold box and VS's collapsed-line shading. Keeping the
    /// text at normal height keeps the outlining arrow and line number full size at the card's top.
    /// </summary>
    internal sealed class DocCommentCardLayer : ILineTransformSource
    {
        private const double Squash = 0.01;
        private const string CollapsedLineShadingLayerName = "OutliningWholeLineHighlighter";

        private readonly List<SnapshotSpan> _cardLines = new List<SnapshotSpan>();
        private readonly Dictionary<FrameworkElement, PlacedCard> _placed = new Dictionary<FrameworkElement, PlacedCard>();
        private bool _tintShown;
        private readonly bool _isPeek;
        private SolidColorBrush _stripBrush;
        private bool _prebuildScheduled;

        /// <summary>Cards kept hidden in the layer after scrolling out of view, ready to show again.</summary>
        private const int MaxHiddenCards = 40;
        private IAdornmentLayer _shadingLayer;
        private bool _shadingLayerMissing;
        private readonly IWpfTextView _view;
        private readonly IOutliningManager _outlining;
        private readonly DocCommentBufferState _state;
        private readonly DocCardCache _cards;
        private readonly IAdornmentLayer _layer;
        private readonly bool _enabled;
        private bool _relayoutScheduled;
        private ITextBuffer _otherDiffSide;

        public DocCommentCardLayer(IWpfTextView view, IOutliningManager outlining)
        {
            _view = view;

            // Inline diffs mix both versions of the text; cards would hide the diff.
            _enabled = !DiffAwareness.IsInlineDiff(view);
            _outlining = outlining;
            _state = DocCommentBufferState.Get(view.TextBuffer);
            _cards = new DocCardCache(view);
            _layer = view.GetAdornmentLayer(DocCommentCardLayerProvider.LayerName);

            _view.LayoutChanged += OnLayoutChanged;
            _view.Selection.SelectionChanged += OnSelectionChanged;
            _view.ViewportWidthChanged += OnChanged;
            _view.BackgroundBrushChanged += OnBackgroundChanged;
            _isPeek = view.Roles.Contains(PredefinedTextViewRoles.EmbeddedPeekTextView);
            _view.GotAggregateFocus += OnFocusChanged;
            _view.LostAggregateFocus += OnFocusChanged;
            _view.TextBuffer.Changed += OnBufferChanged;
            _state.Changed += OnChanged;
            InheritedDocStore.Get(view.TextBuffer).Changed += OnChanged;
            DocTheme.Changed += OnThemeChanged;
            if (_outlining != null)
            {
                _outlining.RegionsCollapsed += OnChanged;
                _outlining.RegionsExpanded += OnChanged;
            }

            _view.Closed += OnClosed;
        }

        public LineTransform GetLineTransform(ITextViewLine line, double yPosition, ViewRelativePosition placement) =>
            Guard.Run("Card spacing", () => LineTransformFor(line), line.DefaultLineTransform);

        private LineTransform LineTransformFor(ITextViewLine line)
        {
            if (!_enabled)
            {
                return line.DefaultLineTransform;
            }

            if (DocCardVisibility.UsesOverlay(_view))
            {
                return OverlayTransform(line);
            }

            var block = BlockStartingOn(line);
            if (block == null || !DocCardVisibility.IsShowingCard(_view, _state, _outlining, block))
            {
                return line.DefaultLineTransform;
            }

            if (!line.IsFirstTextViewLineForSnapshotLine)
            {
                // Word-wrapped continuation of the comment's first line: it would appear below
                // the card, so shrink it away.
                return new LineTransform(0, 0, Squash);
            }

            // Text stays one normal line tall at the top; the rest of the card is space below it.
            double cardHeight = _cards.Get(block).DesiredSize.Height;
            return new LineTransform(0, Math.Max(0, cardHeight - LineHeight), 1.0);
        }

        /// <summary>
        /// Overlay mode (side-by-side diffs, where the comment can't be folded): the comment's raw
        /// lines keep their height and the card is drawn over them. If the card is taller, the
        /// difference is added below the comment's last line.
        /// </summary>
        private LineTransform OverlayTransform(ITextViewLine line)
        {
            int lineNumber = line.Start.GetContainingLine().LineNumber;
            var block = DocCommentScanner.BlockEndingAt(line.Snapshot, lineNumber);
            if (block == null || !line.IsLastTextViewLineForSnapshotLine || !DocCardVisibility.IsShowingCard(_view, _state, _outlining, block))
            {
                return line.DefaultLineTransform;
            }

            double rawHeight = (block.EndLine - block.StartLine + 1) * LineHeight;
            return new LineTransform(0, Math.Max(0, _cards.Get(block).DesiredSize.Height - rawHeight), 1.0);
        }

        private double LineHeight => _view.FormattedLineSource?.LineHeight ?? DocTheme.Current.FontSize * 1.35;

        private static DocCommentBlock BlockStartingOn(ITextViewLine line)
        {
            int lineNumber = line.Start.GetContainingLine().LineNumber;
            return DocCommentScanner.BlockStartingAt(line.Snapshot, lineNumber);
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Guard.Run("Drawing cards", Redraw);

        /// <summary>
        /// The selection only matters to the cards' tint. Moving the caret also counts as a
        /// selection change (an empty one), and needs no redraw unless a tint is showing.
        /// </summary>
        private void OnSelectionChanged(object sender, EventArgs e)
        {
            if (_view.InLayout || (_view.Selection.IsEmpty && !_tintShown)) return;
            Guard.Run("Drawing cards", Redraw);
        }

        /// <summary>
        /// Place the cards for the comments on screen. This runs on every layout, which includes
        /// every step of a scroll, so it only visits the comments in view, reads the collapsed
        /// folds once, and keeps cards in the layer: taking a card out and putting it back makes
        /// WPF lay out its text again. Cards that leave the view are hidden, ready for when they
        /// come back; cards that were rebuilt or dropped from the cache are removed.
        /// </summary>
        private void Redraw()
        {
            _cardLines.Clear();
            _tintShown = false;
            var lines = _view.TextViewLines;
            if (!_enabled || _view.IsClosed || lines == null || lines.Count == 0)
            {
                RemoveAllCards();
                return;
            }

            WatchOtherDiffSide();

            var selection = _view.Selection.IsEmpty ? null : _view.Selection.SelectedSpans;
            bool overlay = DocCardVisibility.UsesOverlay(_view);
            var shown = new HashSet<FrameworkElement>();
            var theme = DocTheme.Current;
            var coverFill = ViewBackground(theme);

            var formatted = lines.FormattedSpan;
            var snapshot = formatted.Snapshot;
            var folds = CollapsedFolds.Read(_outlining, formatted);
            int firstLine = formatted.Start.GetContainingLine().LineNumber;
            int lastLine = formatted.End.GetContainingLine().LineNumber;

            foreach (var block in DocCommentScanner.BlocksInLines(snapshot, firstLine, lastLine))
            {
                // Folded, the card sits on the comment's first line. As an overlay it covers all
                // of the comment's lines, and must be drawn even when the first one has scrolled off.
                int anchorLine = overlay ? Math.Max(block.StartLine, firstLine) : block.StartLine;
                if (anchorLine < firstLine)
                {
                    continue;
                }

                // The comment's first character, or for an overlay whose first line has scrolled
                // off, the first character of its first line on screen.
                var anchor = anchorLine == block.StartLine ? block.Span.Start : FirstNonBlank(snapshot.GetLineFromLineNumber(anchorLine));
                var line = lines.GetTextViewLineContainingBufferPosition(anchor);
                if (line == null || !DocCardVisibility.IsShowingCard(_view, _state, _outlining, block, folds))
                {
                    continue;
                }

                var card = _cards.Get(block);
                if (!shown.Add(card))
                {
                    continue;
                }

                double top = line.Top;
                double height = line.TextBottom - line.Top + 1;
                if (overlay)
                {
                    top -= (anchorLine - block.StartLine) * LineHeight;
                    double rawHeight = (block.EndLine - block.StartLine + 1) * LineHeight;
                    card.MinHeight = Math.Max(0, rawHeight - card.Margin.Top - card.Margin.Bottom);
                    height = Math.Max(card.DesiredSize.Height, rawHeight);
                }

                // Editor-coloured strip under the card, hiding the raw text (from the very top, so
                // no sliver of selection shows above it). It starts where the comment starts: the
                // indentation to its left is only whitespace, and covering it would break the
                // vertical structure guide lines there. Clicks pass through it to the editor; the
                // controller then moves the caret off the card.
                double left = line.GetCharacterBounds(anchor).Left;

                if (!_placed.TryGetValue(card, out var parts))
                {
                    parts = new PlacedCard(new Rectangle { IsHitTestVisible = false });
                    _placed.Add(card, parts);
                    AddToLayer(parts.Cover);
                    AddToLayer(card);
                }

                parts.Cover.Visibility = Visibility.Visible;
                card.Visibility = Visibility.Visible;
                parts.Cover.Fill = coverFill;
                parts.Cover.Width = Math.Max(_view.ViewportRight, line.Right) + 16 - left;
                parts.Cover.Height = height;
                Canvas.SetLeft(parts.Cover, left);
                Canvas.SetTop(parts.Cover, top);
                Canvas.SetLeft(card, left);
                Canvas.SetTop(card, top);
                _cardLines.Add(line.Extent);

                // A selection that includes the comment tints the card, so it reads as selected.
                if (selection != null && selection.IntersectsWith(block.Span))
                {
                    if (parts.Tint == null)
                    {
                        parts.Tint = new Rectangle { Opacity = 0.45, RadiusX = 6, RadiusY = 6, IsHitTestVisible = false };
                        AddToLayer(parts.Tint); // Added after the card, so drawn over it.
                    }

                    var margin = card.Margin;
                    parts.Tint.Fill = theme.SelectionBackground;
                    parts.Tint.Width = card.Width;
                    parts.Tint.Height = Math.Max(0, card.DesiredSize.Height - margin.Top - margin.Bottom);
                    Canvas.SetLeft(parts.Tint, left + margin.Left);
                    Canvas.SetTop(parts.Tint, top + margin.Top);
                    _tintShown = true;
                }
                else if (parts.Tint != null)
                {
                    _layer.RemoveAdornment(parts.Tint);
                    parts.Tint = null;
                }
            }

            PutAwayCardsNotShown(shown);

            // VS's shading may be added before or after this handler runs, so clear it now and
            // again straight after (Normal priority runs before the next render).
            RemoveCollapsedLineShading();
            Guard.Later(_view.VisualElement.Dispatcher, DispatcherPriority.Normal, "Drawing cards", new Action(RemoveCollapsedLineShading));

            if (!overlay)
            {
                SchedulePrebuild();
            }
        }

        /// <summary>
        /// The colour on screen behind this editor's text, for the strip that hides the raw first
        /// line. Peek Definition's editor is see-through: Peek paints its own "Peek Background"
        /// (or "Peek Background Unfocused") behind it, read from the editor's colour settings, so
        /// the strip uses the same colour. Other editors paint their own background.
        /// </summary>
        private Brush ViewBackground(DocTheme theme)
        {
            var editor = ((SolidColorBrush)theme.EditorBackground).Color;
            Color? color = _isPeek ? PeekBackground() : null;
            color ??= (_view.Background as SolidColorBrush)?.Color;
            if (color == null || color.Value.A == 0)
            {
                return theme.EditorBackground;
            }

            var c = color.Value;
            if (c.A < 255)
            {
                // Partly see-through: what shows is this colour over the editor's background.
                double a = c.A / 255.0;
                c = Color.FromRgb(
                    (byte)Math.Round(editor.R + (c.R - editor.R) * a),
                    (byte)Math.Round(editor.G + (c.G - editor.G) * a),
                    (byte)Math.Round(editor.B + (c.B - editor.B) * a));
            }

            if (_stripBrush == null || _stripBrush.Color != c)
            {
                _stripBrush = new SolidColorBrush(c);
                _stripBrush.Freeze();
            }

            return _stripBrush;
        }

        private Color? PeekBackground()
        {
            var formatMap = EditorServices.Instance?.FormatMapService?.GetEditorFormatMap(_view);
            var properties = formatMap?.GetProperties(_view.HasAggregateFocus ? "Peek Background" : "Peek Background Unfocused");
            if (properties == null) return null;
            if (properties[EditorFormatDefinition.BackgroundBrushId] is SolidColorBrush brush) return brush.Color;
            if (properties[EditorFormatDefinition.BackgroundColorId] is Color color) return color;
            return null;
        }

        private void OnBackgroundChanged(object sender, BackgroundBrushChangedEventArgs e) => Guard.Run("Drawing cards", Redraw);

        /// <summary>Peek's background differs when it has focus.</summary>
        private void OnFocusChanged(object sender, EventArgs e)
        {
            if (_isPeek) Guard.Run("Drawing cards", Redraw);
        }

        private static SnapshotPoint FirstNonBlank(ITextSnapshotLine line)
        {
            string text = line.GetText();
            return line.Start + (text.Length - text.TrimStart().Length);
        }

        /// <summary>
        /// Cards no longer on screen. Those still current (scrolled out of view) are hidden, so
        /// scrolling back only shows them again; rebuilt or dropped ones are removed, and so are
        /// the oldest hidden ones beyond a limit.
        /// </summary>
        private void PutAwayCardsNotShown(HashSet<FrameworkElement> shown)
        {
            List<FrameworkElement> remove = null;
            int hidden = 0;
            foreach (var pair in _placed)
            {
                if (shown.Contains(pair.Key))
                {
                    continue;
                }

                if (!_cards.Holds(pair.Key) || ++hidden > MaxHiddenCards)
                {
                    (remove ??= new List<FrameworkElement>()).Add(pair.Key);
                    continue;
                }

                pair.Key.Visibility = Visibility.Hidden; // Hidden, not Collapsed: keeps its layout, so showing it again is free.
                pair.Value.Cover.Visibility = Visibility.Hidden;
                if (pair.Value.Tint != null)
                {
                    _layer.RemoveAdornment(pair.Value.Tint);
                    pair.Value.Tint = null;
                }
            }

            if (remove != null)
            {
                foreach (var card in remove) RemoveCard(card);
            }
        }

        /// <summary>
        /// Build cards for comments just outside the view while VS is idle, one per idle moment,
        /// so scrolling to them finds them ready instead of building them mid-scroll.
        /// </summary>
        private void SchedulePrebuild()
        {
            if (_prebuildScheduled || _view.IsClosed)
            {
                return;
            }

            _prebuildScheduled = true;
            Guard.Later(_view.VisualElement.Dispatcher, DispatcherPriority.ApplicationIdle, "Preparing cards", new Action(PrebuildNext));
        }

        private void PrebuildNext()
        {
            _prebuildScheduled = false;
            var lines = _view.TextViewLines;
            if (_view.IsClosed || _view.InLayout || lines == null || lines.Count == 0 || DocCardVisibility.UsesOverlay(_view))
            {
                return;
            }

            var snapshot = lines.FormattedSpan.Snapshot;
            if (snapshot != _view.TextSnapshot)
            {
                return; // Mid-edit; the next layout schedules this again.
            }

            int first = lines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
            int last = lines.LastVisibleLine.End.GetContainingLine().LineNumber;
            int reach = Math.Max(20, last - first); // About a screen above and below.

            // Nearest first: below the view (the usual way to read), then above it.
            var below = DocCommentScanner.BlocksInLines(snapshot, last + 1, last + reach);
            var above = DocCommentScanner.BlocksInLines(snapshot, first - reach, first - 1).Reverse();
            foreach (var block in below.Concat(above))
            {
                if (!DocCardVisibility.WantsCard(_view, _state, block) || _cards.Contains(block))
                {
                    continue;
                }

                _cards.Get(block);
                SchedulePrebuild(); // More may be waiting; carry on at the next idle moment.
                return;
            }
        }

        /// <summary>Positioned by <see cref="Redraw"/> on every layout, so the layer never moves or removes them itself.</summary>
        private void AddToLayer(UIElement element) =>
            _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, element, null);

        private void RemoveCard(FrameworkElement card)
        {
            var parts = _placed[card];
            _layer.RemoveAdornment(card);
            _layer.RemoveAdornment(parts.Cover);
            if (parts.Tint != null) _layer.RemoveAdornment(parts.Tint);
            _placed.Remove(card);
        }

        private void RemoveAllCards()
        {
            _layer.RemoveAllAdornments();
            _placed.Clear();
        }

        /// <summary>What's in the layer for one card besides the card: the strip under it and the selection tint.</summary>
        private sealed class PlacedCard
        {
            public PlacedCard(Rectangle cover)
            {
                Cover = cover;
            }

            public Rectangle Cover { get; }

            public Rectangle Tint { get; set; }
        }

        /// <summary>
        /// In a side-by-side diff, whether a comment shows as a card depends on the other side
        /// too, so refresh when that changes. (The diff viewer may not exist yet when this layer
        /// is created, hence finding it on first layout.)
        /// </summary>
        private void WatchOtherDiffSide()
        {
            if (_otherDiffSide != null || !DiffAwareness.IsSideBySideDiff(_view))
            {
                return;
            }

            _otherDiffSide = DiffAwareness.OtherSide(_view);
            if (_otherDiffSide != null)
            {
                _otherDiffSide.Changed += OnOtherDiffSideChanged;
            }
        }

        private void OnOtherDiffSideChanged(object sender, TextContentChangedEventArgs e) =>
            Guard.Run("Drawing cards", () => OnOtherDiffSideChangedCore(sender, e));

        private void OnOtherDiffSideChangedCore(object sender, TextContentChangedEventArgs e)
        {
            ScheduleRelayout();
            DocCommentViewController.For(_view)?.Refresh();
        }

        /// <summary>
        /// VS 2026 shades every line that contains a collapsed fold (its OutliningWholeLineHighlighter
        /// layer, drawn below the text). A card's line always contains our fold over lines 2..N,
        /// so remove that shading for card lines only; other collapsed regions keep theirs.
        /// </summary>
        private void RemoveCollapsedLineShading()
        {
            if (_cardLines.Count == 0 || _view.IsClosed)
            {
                return;
            }

            var layer = CollapsedLineShadingLayer();
            if (layer == null)
            {
                return;
            }

            var snapshot = _view.TextSnapshot;
            foreach (var span in _cardLines)
            {
                if (span.Snapshot == snapshot)
                {
                    layer.RemoveAdornmentsByVisualSpan(span);
                }
            }
        }

        private IAdornmentLayer CollapsedLineShadingLayer()
        {
            if (_shadingLayer == null && !_shadingLayerMissing)
            {
                try
                {
                    _shadingLayer = _view.GetAdornmentLayer(CollapsedLineShadingLayerName);
                }
                catch (ArgumentOutOfRangeException)
                {
                    _shadingLayerMissing = true; // Older or newer VS without this feature.
                }
            }

            return _shadingLayer;
        }

        /// <summary>
        /// Lines around an ordinary edit are re-laid out by the editor anyway (and the card layout
        /// follows). Only an edit to a card's comment can change the card or its height, possibly
        /// under a fold the editor doesn't re-lay out, so only then is a fresh layout forced.
        /// Typing in a raw comment (the usual way to edit one) doesn't need it.
        /// </summary>
        private void OnBufferChanged(object sender, TextContentChangedEventArgs e) =>
            Guard.Run("Drawing cards", () => OnBufferChangedCore(sender, e));

        private void OnBufferChangedCore(object sender, TextContentChangedEventArgs e)
        {
            if (!DocCommentScanner.TouchesComments(e))
            {
                return;
            }

            bool nearCard = DocCardVisibility.UsesOverlay(_view) || EditIsNextToACard(e);
            if (!nearCard && DocCommentScanner.EditKeepsCommentShapes(e))
            {
                return; // Typing in a raw comment.
            }

            _cards.Prune(e.After);
            if (nearCard)
            {
                ScheduleRelayout();
            }
        }

        /// <summary>
        /// A comment on or next to an edited line wants a card. (Next to: an edit there can join
        /// it with another comment, or be what split it in two.)
        /// </summary>
        private bool EditIsNextToACard(TextContentChangedEventArgs e)
        {
            var snapshot = e.After;
            foreach (var change in e.Changes)
            {
                int first = snapshot.GetLineNumberFromPosition(change.NewPosition) - 1;
                int last = snapshot.GetLineNumberFromPosition(Math.Min(change.NewEnd, snapshot.Length)) + 1;
                foreach (var block in DocCommentScanner.BlocksInLines(snapshot, first, last))
                {
                    if (DocCardVisibility.WantsCard(_view, _state, block)) return true;
                }
            }

            return false;
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            _cards.Clear();
            ScheduleRelayout();
        }

        private void OnChanged(object sender, EventArgs e) => ScheduleRelayout();

        /// <summary>Line transforms are only asked for during layout, so force one for the visible lines.</summary>
        private void ScheduleRelayout()
        {
            if (_relayoutScheduled || _view.IsClosed)
            {
                return;
            }

            _relayoutScheduled = true;
            Guard.Later(_view.VisualElement.Dispatcher, DispatcherPriority.Normal, "Drawing cards", new Action(() =>
            {
                _relayoutScheduled = false;
                if (_view.IsClosed) return;
                if (_view.InLayout)
                {
                    ScheduleRelayout();
                    return;
                }

                var lines = _view.TextViewLines;
                if (lines == null || lines.Count == 0) return;
                var first = lines.FirstVisibleLine;
                _view.DisplayTextLineContainingBufferPosition(first.Start, first.Top - _view.ViewportTop, ViewRelativePosition.Top);
            }));
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Selection.SelectionChanged -= OnSelectionChanged;
            if (_otherDiffSide != null) _otherDiffSide.Changed -= OnOtherDiffSideChanged;
            _view.ViewportWidthChanged -= OnChanged;
            _view.BackgroundBrushChanged -= OnBackgroundChanged;
            _view.GotAggregateFocus -= OnFocusChanged;
            _view.LostAggregateFocus -= OnFocusChanged;
            _view.TextBuffer.Changed -= OnBufferChanged;
            _state.Changed -= OnChanged;
            InheritedDocStore.Get(_view.TextBuffer).Changed -= OnChanged;
            DocTheme.Changed -= OnThemeChanged;
            if (_outlining != null)
            {
                _outlining.RegionsCollapsed -= OnChanged;
                _outlining.RegionsExpanded -= OnChanged;
            }

            _view.Closed -= OnClosed;
            _cards.Clear();
        }
    }
}
