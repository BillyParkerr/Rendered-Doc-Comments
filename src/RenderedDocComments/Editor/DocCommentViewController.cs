using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using Microsoft.VisualStudio.Utilities;
using RenderedDocComments.Parsing;
using RenderedDocComments.Diagnostics;

namespace RenderedDocComments.Editor
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("CSharp")]
    [TextViewRole(Roles.Document)]
    [TextViewRole(Roles.Peek)]
    [TextViewRole(Roles.LeftDiff)]
    [TextViewRole(Roles.RightDiff)]
    internal sealed class DocCommentViewCreationListener : IWpfTextViewCreationListener
    {
        [Import]
        internal EditorServices Services { get; set; }

        public void TextViewCreated(IWpfTextView textView)
        {
            // Inline diffs mix both versions of the text; cards would hide the diff, so leave them alone.
            if (DiffAwareness.IsInlineDiff(textView))
            {
                return;
            }

            var outlining = Services.OutliningManagerService.GetOutliningManager(textView);
            textView.Properties.GetOrCreateSingletonProperty(() => new DocCommentViewController(textView, outlining));
        }
    }

    /// <summary>
    /// Keeps one editor in line with the rendered/raw state: rendered comments have lines 2..N
    /// folded (so the card shows), raw ones are open. Also handles the caret, Find, and VS's
    /// own outlining commands acting on doc comments.
    /// </summary>
    internal sealed class DocCommentViewController
    {
        private readonly IWpfTextView _view;
        private readonly IOutliningManager _outlining;
        private readonly DocCommentBufferState _state;
        private bool _syncing;
        private bool _syncScheduled;
        private bool _syncEverything;
        private readonly List<ITrackingSpan> _syncScopes = new List<ITrackingSpan>();
        private bool _skipping;
        private bool _warnedOutliningOff;
        private int _lastCaretLine = -1;

        public DocCommentViewController(IWpfTextView view, IOutliningManager outlining)
        {
            _view = view;
            _outlining = outlining;
            _state = DocCommentBufferState.Get(view.TextBuffer);

            _state.Changed += OnStateChanged;
            _view.Caret.PositionChanged += OnCaretMoved;
            _view.TextBuffer.Changed += OnBufferChanged;
            _view.GotAggregateFocus += OnGotFocus;
            _view.Closed += OnClosed;

            if (_outlining != null)
            {
                _outlining.RegionsChanged += OnRegionsChanged;
                _outlining.RegionsExpanded += OnRegionsExpanded;
                _outlining.RegionsCollapsed += OnRegionsCollapsed;
                _outlining.OutliningEnabledChanged += OnOutliningEnabledChanged;
            }

            ScheduleSync();
        }

        public static DocCommentViewController For(ITextView view) =>
            view.Properties.TryGetProperty(typeof(DocCommentViewController), out DocCommentViewController controller) ? controller : null;

        /// <summary>Toggle the comment under the caret, or the one directly above the caret's line.</summary>
        public void ToggleAtCaret() =>
            Guard.Run("Updating comment folds", () => ToggleAtCaretCore());

        private void ToggleAtCaretCore()
        {
            var caret = _view.Caret.Position.BufferPosition;
            var block = DocCommentScanner.FindForCaretLine(caret.Snapshot, caret.GetContainingLine().LineNumber);
            if (block == null)
            {
                DocCommentActions.SetStatus("No documentation comment on or above this line.");
                return;
            }

            Toggle(block);
        }

        public void ToggleAll() =>
            Guard.Run("Updating comment folds", () => ToggleAllCore());

        private void ToggleAllCore()
        {
            bool render = !_state.AnyRendered();
            if (render)
            {
                var caret = _view.Caret.Position.BufferPosition;
                var block = DocCommentScanner.BlockAtLine(caret.Snapshot, caret.GetContainingLine().LineNumber);
                if (block != null) MoveCaretBelow(block);
            }

            _state.SetAll(render);
            DocCommentActions.SetStatus(render ? "All doc comments rendered." : "All doc comments shown raw.");
        }

        /// <summary>Re-check which comments should be folded into cards (e.g. the other side of a diff changed).</summary>
        public void Refresh() => ScheduleSync();

        /// <summary>Toggle one comment between card and raw (used by the gutter icon).</summary>
        public void ToggleBlock(DocCommentBlock block) => Guard.Run("Switching a comment", () => Toggle(block));

        private void Toggle(DocCommentBlock block, bool raise = true)
        {
            if (IsShowingCard(block))
            {
                _state.SetRendered(block, false, raise);
            }
            else
            {
                // Rendering folds lines 2..N, which can't happen with the caret inside them.
                MoveCaretBelow(block);
                _state.SetRendered(block, true, raise);
            }
        }

        private bool IsShowingCard(DocCommentBlock block) => DocCardVisibility.IsShowingCard(_view, _state, _outlining, block);

        private void MoveCaretBelow(DocCommentBlock block)
        {
            var caret = _view.Caret.Position.BufferPosition;
            if (!block.ContainsLineOf(caret))
            {
                return;
            }

            var snapshot = caret.Snapshot;
            var line = snapshot.GetLineFromLineNumber(Math.Min(block.EndLine + 1, snapshot.LineCount - 1));
            _view.Selection.Clear();
            _view.Caret.MoveTo(new SnapshotPoint(snapshot, line.Start.Position + Indent(line)));
        }

        private static int Indent(ITextSnapshotLine line)
        {
            string text = line.GetText();
            return text.Length - text.TrimStart().Length;
        }

        /// <summary>Re-check every comment's fold (after switching comments, outlining being turned on, and so on).</summary>
        private void ScheduleSync() => ScheduleSync(null);

        /// <summary>
        /// Re-check the folds of the comments in <paramref name="scope"/> (null: the whole file).
        /// Requests are gathered and handled together once the current command has finished.
        /// Moving the caret, for instance, only needs the comments it left and arrived at checked,
        /// rather than every fold in the file on every keystroke.
        /// </summary>
        private void ScheduleSync(SnapshotSpan? scope)
        {
            if (_view.IsClosed)
            {
                return;
            }

            if (scope == null)
            {
                _syncEverything = true;
                _syncScopes.Clear();
            }
            else if (!_syncEverything)
            {
                _syncScopes.Add(scope.Value.Snapshot.CreateTrackingSpan(scope.Value, SpanTrackingMode.EdgeInclusive));
            }

            if (_syncScheduled)
            {
                return;
            }

            _syncScheduled = true;
            Guard.Later(_view.VisualElement.Dispatcher, DispatcherPriority.Normal, "Updating comment folds", new Action(() =>
            {
                _syncScheduled = false;
                bool everything = _syncEverything;
                var scopes = _syncScopes.ToList();
                _syncEverything = false;
                _syncScopes.Clear();

                var snapshot = _view.TextBuffer.CurrentSnapshot;
                if (everything)
                {
                    Sync(new SnapshotSpan(snapshot, 0, snapshot.Length));
                    return;
                }

                foreach (var area in scopes)
                {
                    Sync(area.GetSpan(snapshot));
                }
            }));
        }

        /// <summary>Fold the rendered comments in the area and open the raw ones.</summary>
        private void Sync(SnapshotSpan all)
        {
            if (_view.IsClosed || _outlining == null || !_outlining.Enabled)
            {
                return;
            }

            var snapshot = all.Snapshot;
            var caret = _view.Caret.Position.BufferPosition;

            // In a diff the card is laid over the raw lines, so nothing of ours may be folded.
            bool overlay = DocCardVisibility.UsesOverlay(_view);

            _syncing = true;
            try
            {
                foreach (var collapsed in _outlining.GetCollapsedRegions(all).ToList())
                {
                    if (!(collapsed.Tag is DocCommentRegionTag)) continue;
                    var block = DocCommentScanner.FindIntersecting(snapshot, collapsed.Extent.GetSpan(snapshot));
                    if (overlay || block == null || !DocCardVisibility.WantsCard(_view, _state, block))
                    {
                        _outlining.Expand(collapsed);
                    }
                }

                foreach (var region in _outlining.GetAllRegions(all).ToList())
                {
                    if (overlay || region.IsCollapsed || !(region.Tag is DocCommentRegionTag)) continue;
                    var block = DocCommentScanner.FindIntersecting(snapshot, region.Extent.GetSpan(snapshot));
                    if (block != null && DocCardVisibility.WantsCard(_view, _state, block) && !block.ContainsLineOf(caret))
                    {
                        _outlining.TryCollapse(region);
                    }
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        // Outlining events are gathered and handled together once the command that raised them
        // has finished: bulk commands (Ctrl+M, O / Ctrl+M, L) can raise one event per region,
        // which on its own looks exactly like you toggling a single comment.
        private readonly List<ITrackingSpan> _pendingOpenedCards = new List<ITrackingSpan>();
        private readonly List<ITrackingSpan> _pendingClosedRawFolds = new List<ITrackingSpan>();
        private int _pendingExpandedCount;
        private int _pendingCollapsedCount;
        private bool _outliningFlushScheduled;

        private void OnRegionsExpanded(object sender, RegionsExpandedEventArgs e) =>
            Guard.Run("Updating comment folds", () => OnRegionsExpandedCore(sender, e));

        private void OnRegionsExpandedCore(object sender, RegionsExpandedEventArgs e)
        {
            if (_syncing || e.RemovalPending)
            {
                return;
            }

            var snapshot = _view.TextBuffer.CurrentSnapshot;
            bool held = false;
            foreach (var region in e.ExpandedRegions)
            {
                _pendingExpandedCount++;
                if (!(region.Tag is DocCommentRegionTag)) continue;

                var block = DocCommentScanner.FindIntersecting(snapshot, region.Extent.GetSpan(snapshot));
                if (block == null || !_state.IsRendered(block)) continue;

                // Hold it open until we know what opened it, so a pending sync doesn't fold it again.
                _state.SetTemporarilyRaw(block, raise: false);
                _pendingOpenedCards.Add(snapshot.CreateTrackingSpan(block.Span, SpanTrackingMode.EdgeInclusive));
                held = true;
            }

            if (held) _state.RaiseChanged();
            ScheduleOutliningFlush();
        }

        private void OnRegionsCollapsed(object sender, RegionsCollapsedEventArgs e) =>
            Guard.Run("Updating comment folds", () => OnRegionsCollapsedCore(sender, e));

        private void OnRegionsCollapsedCore(object sender, RegionsCollapsedEventArgs e)
        {
            if (_syncing)
            {
                return;
            }

            // VS's own fold over a comment (from Roslyn) is left to behave normally: collapsing it
            // hides the comment, card or raw, and expanding it brings back whichever was showing.
            // Only our own fold over a raw comment's lines 2..N needs attention (dealt with in the flush).
            var snapshot = _view.TextBuffer.CurrentSnapshot;
            foreach (var region in e.CollapsedRegions)
            {
                _pendingCollapsedCount++;
                if (!(region.Tag is DocCommentRegionTag)) continue;

                var block = DocCommentScanner.FindIntersecting(snapshot, region.Extent.GetSpan(snapshot));
                if (block != null && !_state.IsRendered(block))
                {
                    _pendingClosedRawFolds.Add(snapshot.CreateTrackingSpan(block.Span, SpanTrackingMode.EdgeInclusive));
                }
            }

            ScheduleOutliningFlush();
        }

        private void ScheduleOutliningFlush()
        {
            if (_outliningFlushScheduled || _view.IsClosed)
            {
                return;
            }

            // Normal priority: after the outlining command has finished (Find and Go To Line move
            // the caret as part of the same command), before the next render.
            _outliningFlushScheduled = true;
            Guard.Later(_view.VisualElement.Dispatcher, DispatcherPriority.Normal, "Updating comment folds", new Action(FlushOutlining));
        }

        /// <summary>
        /// Outlining never switches a comment between card and raw (the gutter icon, the pencil
        /// and Ctrl+M, Ctrl+Q do). It only folds and unfolds the comment as a whole.
        /// </summary>
        private void FlushOutlining()
        {
            _outliningFlushScheduled = false;
            int expandedCount = _pendingExpandedCount;
            int collapsedCount = _pendingCollapsedCount;
            var openedCards = _pendingOpenedCards.ToList();
            var closedRawFolds = _pendingClosedRawFolds.ToList();
            _pendingExpandedCount = 0;
            _pendingCollapsedCount = 0;
            _pendingOpenedCards.Clear();
            _pendingClosedRawFolds.Clear();

            if (_view.IsClosed)
            {
                return;
            }

            var snapshot = _view.TextBuffer.CurrentSnapshot;
            var caret = _view.Caret.Position.BufferPosition;
            var collapseWhole = new List<DocCommentBlock>();

            // A card's hidden lines were opened by something other than us:
            //  - caret now inside: Find / Go To opened it to show you something; it stays raw
            //    until the caret leaves;
            //  - a bulk command (Ctrl+M, O / L): put the card back;
            //  - one fold on its own (the margin arrow, Ctrl+M, M): you asked to collapse this
            //    comment, so keep the card and fold the whole comment instead.
            foreach (var span in openedCards)
            {
                var block = DocCommentScanner.FindIntersecting(snapshot, span.GetSpan(snapshot));
                if (block == null || !_state.IsTemporarilyRaw(block) || block.ContainsLineOf(caret)) continue;
                _state.SetRendered(block, true, raise: false);
                if (expandedCount == 1) collapseWhole.Add(block);
            }

            // Our fold over a raw comment's lines 2..N was closed. On its own, that's the margin
            // arrow or Ctrl+M, M: fold the whole comment. As part of a bulk command, the sync
            // just opens it again.
            if (collapsedCount == 1)
            {
                collapseWhole.AddRange(closedRawFolds
                    .Select(s => DocCommentScanner.FindIntersecting(snapshot, s.GetSpan(snapshot)))
                    .Where(b => b != null));
            }

            CollapseWholeComments(collapseWhole);
            _state.RaiseChanged();
        }

        /// <summary>A region that lies entirely within one doc comment's lines (Roslyn's comment fold).</summary>
        private static DocCommentBlock BlockInside(ITextSnapshot snapshot, SnapshotSpan span)
        {
            int startLine = span.Start.GetContainingLine().LineNumber;
            int endLine = span.End.GetContainingLine().LineNumber;
            var block = DocCommentScanner.BlockAtLine(snapshot, startLine);
            return block != null && endLine <= block.EndLine ? block : null;
        }

        /// <summary>Collapse VS's own fold over each of these comments (the normal one-line "/// ..." box).</summary>
        private void CollapseWholeComments(List<DocCommentBlock> blocks)
        {
            if (blocks.Count == 0 || _outlining == null || !_outlining.Enabled)
            {
                return;
            }

            var snapshot = _view.TextBuffer.CurrentSnapshot;
            _syncing = true;
            try
            {
                foreach (var block in blocks)
                {
                    var fold = _outlining.GetAllRegions(block.Span).FirstOrDefault(r =>
                        !r.IsCollapsed
                        && !(r.Tag is DocCommentRegionTag)
                        && r.Extent.GetSpan(snapshot).Start.GetContainingLine().LineNumber == block.StartLine
                        && BlockInside(snapshot, r.Extent.GetSpan(snapshot)) != null);

                    // No fold from VS (e.g. comment outlining turned off in the C# options): nothing to collapse.
                    if (fold != null)
                    {
                        _outlining.TryCollapse(fold);
                    }
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e) =>
            Guard.Run("Updating comment folds", () => OnCaretMovedCore(sender, e));

        private void OnCaretMovedCore(object sender, CaretPositionChangedEventArgs e)
        {
            if (!_skipping)
            {
                HandleLandingOnCard(e);
            }

            var caret = e.NewPosition.BufferPosition;
            int line = caret.GetContainingLine().LineNumber;
            if (line == _lastCaretLine)
            {
                return;
            }

            _lastCaretLine = line;

            if (_state.HasTemporary)
            {
                var left = DocCommentScanner.GetBlocks(caret.Snapshot)
                    .Where(b => _state.IsTemporarilyRaw(b) && !b.ContainsLineOf(caret))
                    .ToList();
                foreach (var block in left)
                {
                    _state.SetRendered(block, true, raise: false);
                }

                if (left.Count > 0)
                {
                    _state.RaiseChanged();
                    return;
                }
            }

            // A rendered comment held open because the caret was in it can fold now. Only the
            // comments the caret left and arrived at can be affected.
            ScheduleSync(CommentAround(e.OldPosition.BufferPosition.TranslateTo(caret.Snapshot, PointTrackingMode.Positive)));
            ScheduleSync(CommentAround(caret));
        }

        /// <summary>The comment on the point's line, or directly above it (as the caret sees it), else just the line.</summary>
        private static SnapshotSpan CommentAround(SnapshotPoint point)
        {
            var snapshot = point.Snapshot;
            int line = point.GetContainingLine().LineNumber;
            var block = DocCommentScanner.FindForCaretLine(snapshot, line);
            int first = block?.StartLine ?? line;
            int last = Math.Max(line, block?.EndLine ?? line);
            return new SnapshotSpan(snapshot.GetLineFromLineNumber(first).Start, snapshot.GetLineFromLineNumber(last).EndIncludingLineBreak);
        }

        /// <summary>
        /// The caret should never sit on a card: typing there would edit hidden text. Once the
        /// move that put it there has finished:
        ///  - if text inside the comment is selected (Find, Go To), show the comment raw;
        ///  - otherwise carry on to the line beyond the card in the direction of travel.
        /// Shift-extended selections are left alone so you can select across cards.
        /// </summary>
        private void HandleLandingOnCard(CaretPositionChangedEventArgs e)
        {
            var landed = e.NewPosition.BufferPosition;
            var block = DocCommentScanner.BlockAtLine(landed.Snapshot, landed.GetContainingLine().LineNumber);
            if (block == null || !IsShowingCard(block))
            {
                return;
            }

            var from = e.OldPosition.BufferPosition;
            bool byMouse = Mouse.LeftButton == MouseButtonState.Pressed;
            Guard.Later(_view.VisualElement.Dispatcher, DispatcherPriority.Normal, "Updating comment folds", new Action(() =>
            {
                if (_view.IsClosed || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    return;
                }

                var snapshot = _view.TextBuffer.CurrentSnapshot;
                var caret = _view.Caret.Position.BufferPosition;
                var current = DocCommentScanner.BlockAtLine(snapshot, caret.GetContainingLine().LineNumber);
                if (current == null || !IsShowingCard(current))
                {
                    return;
                }

                if (!_view.Selection.IsEmpty)
                {
                    // A mouse drag selecting across the card: leave it alone. Opening the card
                    // would change the layout under the mouse and make it flicker.
                    if (byMouse) return;

                    // Find / Go To selected text inside the card: show the comment raw.
                    _state.SetTemporarilyRaw(current);
                    return;
                }

                // A click beside a card goes to the start of the declaration below it; keyboard
                // moves carry on in their direction, keeping the column.
                var old = from.TranslateTo(snapshot, PointTrackingMode.Positive);
                var oldLine = old.GetContainingLine();
                bool cameFromBelow = !byMouse && oldLine.LineNumber > current.EndLine;
                bool cameFromAbove = !byMouse && oldLine.LineNumber < current.StartLine;
                int target = cameFromBelow ? current.StartLine - 1 : current.EndLine + 1;
                if (target < 0) target = current.EndLine + 1;
                if (target >= snapshot.LineCount) target = current.StartLine - 1;
                if (target < 0) return;

                var line = snapshot.GetLineFromLineNumber(target);
                int offset = cameFromAbove || cameFromBelow
                    ? Math.Min(old.Position - oldLine.Start.Position, line.Length)
                    : Indent(line);

                _skipping = true;
                try
                {
                    _view.Caret.MoveTo(new SnapshotPoint(snapshot, line.Start.Position + offset));
                    _view.Caret.EnsureVisible();
                }
                finally
                {
                    _skipping = false;
                }
            }));
        }

        /// <summary>
        /// Closing the Find window hands focus back to the editor with the match still selected
        /// and the caret untouched. If that match is in a comment Find opened, render it again.
        /// (Clicking into the editor instead moves the caret, so that case is left alone.)
        /// </summary>
        private void OnGotFocus(object sender, EventArgs e) =>
            Guard.Run("Updating comment folds", () => OnGotFocusCore(sender, e));

        private void OnGotFocusCore(object sender, EventArgs e)
        {
            if (!_state.HasTemporary || _view.Selection.IsEmpty)
            {
                return;
            }

            var caretAtFocus = _view.Caret.Position.BufferPosition;
            Guard.Later(_view.VisualElement.Dispatcher, DispatcherPriority.Background, "Updating comment folds", new Action(() =>
            {
                if (_view.IsClosed || _view.Selection.IsEmpty || _view.Caret.Position.BufferPosition != caretAtFocus)
                {
                    return;
                }

                var block = DocCommentScanner.GetBlocks(caretAtFocus.Snapshot)
                    .FirstOrDefault(b => _state.IsTemporarilyRaw(b) && b.ContainsLineOf(caretAtFocus));
                if (block != null)
                {
                    MoveCaretBelow(block);
                    _state.SetRendered(block, true);
                }
            }));
        }

        private void OnOutliningEnabledChanged(object sender, OutliningEnabledEventArgs e) =>
            Guard.Run("Updating comment folds", () => OnOutliningEnabledChangedCore(sender, e));

        private void OnOutliningEnabledChangedCore(object sender, OutliningEnabledEventArgs e)
        {
            if (!e.Enabled && !_warnedOutliningOff)
            {
                _warnedOutliningOff = true;
                DocCommentActions.SetStatus("Outlining is off in this file, so doc comments show raw. Edit > Outlining > Start Automatic Outlining turns it back on.");
            }

            ScheduleSync();
        }

        private void OnStateChanged(object sender, EventArgs e) => ScheduleSync();

        // Folds only need re-checking when an edit touched a comment (other folds are tracked by VS).
        private void OnBufferChanged(object sender, TextContentChangedEventArgs e) =>
            Guard.Run("Updating comment folds", () => OnBufferChangedCore(sender, e));

        private void OnBufferChangedCore(object sender, TextContentChangedEventArgs e)
        {
            if (DocCommentScanner.TouchesComments(e) && !DocCommentScanner.EditKeepsCommentShapes(e)) ScheduleSync();
        }

        // New or reshaped folds (ours after an edit, or VS's) only need checking where they changed.
        private void OnRegionsChanged(object sender, RegionsChangedEventArgs e) => ScheduleSync(e.AffectedSpan);

        private void OnClosed(object sender, EventArgs e)
        {
            // Declarations may have been edited since the last toggle: store the current keys.
            Guard.Run("Remembering comment views", _state.Remember);
            _state.Changed -= OnStateChanged;
            _view.Caret.PositionChanged -= OnCaretMoved;
            _view.TextBuffer.Changed -= OnBufferChanged;
            _view.GotAggregateFocus -= OnGotFocus;
            _view.Closed -= OnClosed;

            if (_outlining != null)
            {
                _outlining.RegionsChanged -= OnRegionsChanged;
                _outlining.RegionsExpanded -= OnRegionsExpanded;
                _outlining.RegionsCollapsed -= OnRegionsCollapsed;
                _outlining.OutliningEnabledChanged -= OnOutliningEnabledChanged;
            }
        }
    }
}
