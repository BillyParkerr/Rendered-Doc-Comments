using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using RenderedDocComments.Diagnostics;
using RenderedDocComments.Navigation;
using RenderedDocComments.Options;
using RenderedDocComments.Parsing;
using RenderedDocComments.Rendering;

namespace RenderedDocComments.Editor
{
    /// <summary>
    /// The quick documentation popup (Ctrl+Shift+F1, or hover when that option is on): the
    /// rendered card for the symbol at the cursor, under its name. Links in it show the linked
    /// symbol's docs in the same popup, with a Back link; the symbol's name goes to its definition.
    /// It closes on Escape, when the cursor moves, on typing, and when the editor loses focus.
    /// A hover popup also closes shortly after the mouse leaves the name, unless the mouse gets
    /// to the popup first; clicking in it pins it, so it then stays like a shortcut popup.
    /// </summary>
    internal sealed class QuickDocPopup : IQuickDocHandler
    {
        /// <summary>The editor's popup space for this popup (see <see cref="QuickDocCommandFilterProvider"/>).</summary>
        public const string SpaceName = "RenderedDocComments.QuickDoc";

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        private readonly IWpfTextView _view;
        private readonly ISpaceReservationManager _space;
        /// <summary>
        /// The popup's content. VS's popup host draws the frame around it (background, border,
        /// rounded corners and shadow, as for its own tooltips), so the card goes in bare.
        /// </summary>
        private readonly Border _root = new Border();
        private readonly ScrollViewer _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };

        /// <summary>
        /// For a hover popup: a short pause before closing once the mouse is away from the name,
        /// the popup and the way between them (see <see cref="MouseNearby"/>), so a quick wobble
        /// doesn't close it.
        /// </summary>
        private readonly DispatcherTimer _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };

        private readonly Stack<QuickDocTarget> _back = new Stack<QuickDocTarget>();
        private ISpaceReservationAgent _agent;
        private ITrackingSpan _anchor;

        /// <summary>Opened by hovering and not clicked in yet: it closes when the mouse moves away.</summary>
        private bool _hover;
        private Document _document;
        private QuickDocTarget _shown;

        /// <summary>Bumped by every new request and by closing, so stale lookups are dropped.</summary>
        private int _request;

        private QuickDocPopup(IWpfTextView view)
        {
            _view = view;
            _space = view.GetSpaceReservationManager(SpaceName);
            _space.AgentChanged += OnAgentChanged;

            Microsoft.VisualStudio.PlatformUI.ImageThemingUtilities.SetThemeScrollBars(_scroll, true); // VS's scroll bars, not WPF's.
            _root.Child = _scroll;
            _root.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    Guard.Run("Closing quick documentation", () => Dismiss());
                }
            };
            _root.MouseUp += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.XButton1) Guard.Run("Quick documentation: back", Back); // The mouse's Back button.
            };

            // Hover popups: the mouse reaching the popup keeps it; leaving it starts the countdown.
            _root.MouseEnter += (s, e) => _leaveTimer.Stop();
            _root.MouseLeave += (s, e) => Guard.Run("Quick documentation: mouse left", StartLeaving);

            // Clicking in a hover popup (a link, or to select text) pins it.
            _root.PreviewMouseDown += (s, e) =>
            {
                _hover = false;
                _leaveTimer.Stop();
            };
            _leaveTimer.Tick += (s, e) => Guard.Run("Closing quick documentation", () =>
            {
                _leaveTimer.Stop();
                if (_hover && _agent != null && !_root.IsMouseOver && !MouseNearby(Mouse.GetPosition(_view.VisualElement))) Dismiss();
            });
            view.VisualElement.MouseMove += (s, e) => Guard.Run("Quick documentation: mouse moved", () =>
            {
                if (!_hover || _agent == null) return;
                if (MouseNearby(e.GetPosition(_view.VisualElement))) _leaveTimer.Stop();
                else if (!_leaveTimer.IsEnabled) _leaveTimer.Start();
            });
            view.VisualElement.MouseLeave += (s, e) => Guard.Run("Quick documentation: mouse left", StartLeaving);

            view.Caret.PositionChanged += (s, e) => Guard.Run("Closing quick documentation", () => Dismiss());
            view.TextBuffer.Changed += (s, e) => Guard.Run("Closing quick documentation", () => Dismiss());
            view.LostAggregateFocus += (s, e) => Guard.Later(view.VisualElement.Dispatcher, DispatcherPriority.Background, "Closing quick documentation", () =>
            {
                // Clicking text in the popup moves focus there; that isn't leaving.
                if (!_view.IsClosed && !_view.HasAggregateFocus && !_root.IsKeyboardFocusWithin) Dismiss();
            });
            view.Closed += (s, e) => Guard.Run("Closing quick documentation", () => Dismiss());
        }

        private static JoinableTaskFactory Jtf => ThreadHelper.JoinableTaskContext.Factory;

        /// <summary>Open and staying put: opened with the shortcut, or a hover popup that was clicked in.</summary>
        public bool IsPinned => _agent != null && !_hover;

        /// <summary>The view's popup, if it has ever had one.</summary>
        public static QuickDocPopup TryGet(ITextView view) =>
            view.Properties.TryGetProperty(typeof(QuickDocPopup), out QuickDocPopup popup) ? popup : null;

        public static QuickDocPopup For(IWpfTextView view) =>
            view.Properties.GetOrCreateSingletonProperty(() => new QuickDocPopup(view));

        /// <summary>Close the view's popup if it has one open. True if it did.</summary>
        public static bool TryDismiss(ITextView view) => TryGet(view)?.Dismiss() == true;

        /// <summary>The shortcut: show the docs for the name at the cursor, or close the popup if it's open.</summary>
        public void ToggleAtCaret()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Dismiss())
            {
                return;
            }

            var point = _view.Caret.Position.Point.GetPoint(b => b.ContentType.IsOfType("CSharp"), PositionAffinity.Successor);
            if (point == null)
            {
                DocCommentActions.SetStatus("Quick documentation works in C# code.");
                return;
            }

            int request = ++_request;
            var snapshot = point.Value.Snapshot;
            int position = point.Value.Position;
            Jtf.RunAsync(async () =>
            {
                try
                {
                    using (var cancellation = new CancellationTokenSource(Timeout))
                    {
                        await TaskScheduler.Default;
                        var document = snapshot.GetOpenDocumentInCurrentContextWithChanges(); // Off the UI thread: it forks the solution.
                        var target = document == null ? null : await QuickDocResolver.AtPositionAsync(document, position, cancellation.Token);

                        await Jtf.SwitchToMainThreadAsync(cancellation.Token);
                        if (request != _request || _view.IsClosed)
                        {
                            return; // The cursor moved, or the file changed, while looking.
                        }

                        if (document == null) DocCommentActions.SetStatus("Quick documentation needs the file to be part of a C# project.");
                        else if (target == null) DocCommentActions.SetStatus("No documentation here: put the cursor on a name.");
                        else Show(document, target, new SnapshotSpan(snapshot, target.Span.Start, target.Span.Length), hover: false);
                    }
                }
                catch (OperationCanceledException)
                {
                    await Jtf.SwitchToMainThreadAsync();
                    if (request == _request) DocCommentActions.SetStatus("Looking up the documentation took too long and was cancelled.");
                }
                catch (Exception ex) when (!Guard.IsCritical(ex))
                {
                    Guard.Report("Quick documentation", ex);
                }
            }).FileAndForget("RenderedDocComments/QuickDoc");
        }

        /// <summary>Show a symbol's docs under its name (<paramref name="span"/>), replacing anything shown.</summary>
        /// <param name="hover">Opened by hovering: it closes when the mouse moves away from the name and the popup.</param>
        public void Show(Document document, QuickDocTarget target, SnapshotSpan span, bool hover)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_view.IsClosed)
            {
                return;
            }

            var current = span.TranslateTo(span.Snapshot.TextBuffer.CurrentSnapshot, SpanTrackingMode.EdgeExclusive);
            var visual = _view.BufferGraph.MapUpToSnapshot(current, SpanTrackingMode.EdgeExclusive, _view.TextSnapshot);
            if (visual.Count == 0)
            {
                return; // Not visible in this view (e.g. folded away).
            }

            _request++;
            _document = document;
            _back.Clear();
            _hover = hover;
            _anchor = _view.TextSnapshot.CreateTrackingSpan(visual[0], SpanTrackingMode.EdgeExclusive);
            Display(target);
        }

        /// <summary>Close the popup. True if it was open.</summary>
        public bool Dismiss()
        {
            _request++;
            if (_agent == null)
            {
                return false;
            }

            bool hadFocus = _root.IsKeyboardFocusWithin;
            var agent = _agent;
            _agent = null;
            _space.RemoveAgent(agent);
            Clear();

            // Focus was in the popup's text: give it back to the editor rather than to nothing.
            if (hadFocus && !_view.IsClosed)
            {
                _view.VisualElement.Focus();
            }

            return true;
        }

        /// <summary>A hover popup the mouse left: close it soon, unless the mouse comes back or reaches it.</summary>
        private void StartLeaving()
        {
            if (_hover && _agent != null && !_leaveTimer.IsEnabled)
            {
                _leaveTimer.Start();
            }
        }

        /// <summary>
        /// The mouse (in the view's coordinates) is over the name the popup is for, or on its way
        /// to the popup: on the name's line or in the gap before the popup, across the popup's
        /// width. Crossing other text on the way there doesn't count as leaving.
        /// </summary>
        private bool MouseNearby(Point mouse)
        {
            if (_anchor == null || _view.IsClosed || _view.InLayout || _view.TextViewLines == null)
            {
                return false;
            }

            var anchor = _anchor.GetSpan(_view.TextSnapshot);
            var line = _view.TextViewLines.GetTextViewLineContainingBufferPosition(anchor.Start);
            if (line == null || !_root.IsVisible || PresentationSource.FromVisual(_root) == null)
            {
                return false;
            }

            // The name's line, in the view's coordinates.
            double lineTop = line.Top - _view.ViewportTop, lineBottom = line.Bottom - _view.ViewportTop;
            double nameLeft = line.GetCharacterBounds(anchor.Start).Left - _view.ViewportLeft;
            double nameRight = line.GetCharacterBounds(anchor.End > anchor.Start ? anchor.End - 1 : anchor.Start).Right - _view.ViewportLeft;

            // The popup, in the same coordinates (it's a window of its own).
            var popupTopLeft = _view.VisualElement.PointFromScreen(_root.PointToScreen(new Point(0, 0)));
            var popupBottomRight = _view.VisualElement.PointFromScreen(_root.PointToScreen(new Point(_root.ActualWidth, _root.ActualHeight)));

            double left = Math.Min(nameLeft, popupTopLeft.X), right = Math.Max(nameRight, popupBottomRight.X);
            if (mouse.X < left || mouse.X > right)
            {
                return false;
            }

            if (mouse.Y >= lineTop && mouse.Y <= lineBottom)
            {
                // On the name's line: over the name itself, or beside it within the popup's width.
                return true;
            }

            // Between the line and the popup, whichever side of the line it opened on.
            return popupTopLeft.Y >= lineTop
                ? mouse.Y > lineBottom && mouse.Y <= popupTopLeft.Y + 1
                : mouse.Y < lineTop && mouse.Y >= popupBottomRight.Y - 1;
        }

        private void Clear()
        {
            _leaveTimer.Stop();
            _scroll.Content = null;
            _back.Clear();
            _shown = null;
            _document = null;
            _anchor = null;
        }

        private void OnAgentChanged(object sender, SpaceReservationAgentChangedEventArgs e)
        {
            // The editor removed it.
            if (_agent != null && e.OldAgent == _agent && e.NewAgent != _agent)
            {
                _agent = null;
                _request++;
                Clear();
            }
        }

        private void Display(QuickDocTarget target)
        {
            bool hadFocus = _root.IsKeyboardFocusWithin;
            _shown = target;

            _scroll.MaxHeight = Math.Max(220, Math.Min(620, SystemParameters.WorkArea.Height * 0.55));

            string back = _back.Count > 0 ? _back.Peek().Content.Title : null;
            var card = (Border)DocCommentRenderer.CreateQuickDocCard(target.Content, Math.Min(ExtensionOptions.MaxCardWidth, 640), back, this);
            card.Background = null; // VS's popup frame is the only frame: no card border or background inside it.
            card.BorderThickness = new Thickness(0);
            _scroll.Content = card;
            _scroll.ScrollToTop();

            // Mouse-leave closing is ours (see _leaveTimer), not the editor's, so the mouse can reach the popup.
            var style = PopupStyles.None;
            if (_agent == null)
            {
                _agent = _space.CreatePopupAgent(_anchor, style, _root);
                _space.AddAgent(_agent);
            }
            else
            {
                _space.UpdatePopupAgent(_agent, _anchor, style);
            }

            // A link was clicked in the old card's text, which had focus and is now gone.
            if (hadFocus)
            {
                _view.VisualElement.Focus();
            }
        }

        /// <summary>A link in the popup: show that symbol's docs here, with a Back link.</summary>
        public void GoToDefinition(string cref)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var document = _document;
            if (document == null || _shown == null)
            {
                return;
            }

            int request = ++_request;
            Jtf.RunAsync(async () =>
            {
                try
                {
                    using (var cancellation = new CancellationTokenSource(Timeout))
                    {
                        await TaskScheduler.Default;
                        var compilation = await document.Project.GetCompilationAsync(cancellation.Token);
                        var target = QuickDocResolver.ForId(cref, compilation, cancellation.Token);

                        await Jtf.SwitchToMainThreadAsync(cancellation.Token);
                        if (request != _request || _agent == null)
                        {
                            return;
                        }

                        if (target == null)
                        {
                            DocCommentActions.SetStatus("Couldn't find " + CrefFormatter.Format(cref) + ".");
                            return;
                        }

                        _back.Push(_shown);
                        Display(target);
                    }
                }
                catch (Exception ex) when (!Guard.IsCritical(ex))
                {
                    if (!(ex is OperationCanceledException)) Guard.Report("Quick documentation: following a link", ex);
                }
            }).FileAndForget("RenderedDocComments/QuickDocLink");
        }

        /// <summary>A link's tooltip: the linked symbol's signature.</summary>
        public void Describe(string cref, Action<string> onDescribed)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var document = _document;
            if (document == null)
            {
                return;
            }

            Jtf.RunAsync(async () =>
            {
                string description = cref;
                try
                {
                    using (var cancellation = new CancellationTokenSource(Timeout))
                    {
                        await TaskScheduler.Default;
                        var compilation = await document.Project.GetCompilationAsync(cancellation.Token);
                        var symbol = compilation == null ? null
                            : DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, compilation) ?? DocumentationCommentId.GetFirstSymbolForReferenceId(cref, compilation);
                        description = symbol == null ? cref + "  (not found)" : CrefResolver.Describe(symbol);
                    }
                }
                catch (Exception ex) when (!Guard.IsCritical(ex))
                {
                    if (!(ex is OperationCanceledException)) Guard.Report("Quick documentation: describing a link", ex);
                }

                await Jtf.SwitchToMainThreadAsync();
                onDescribed(description);
            }).FileAndForget("RenderedDocComments/QuickDocDescribe");
        }

        public void GoToShownDefinition()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var symbol = _shown?.Symbol;
            var project = _document?.Project;
            if (symbol == null || project == null)
            {
                return;
            }

            Dismiss();
            DocCommentNavigator.GoToSymbol(symbol, project);
        }

        public void Back()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_agent != null && _back.Count > 0)
            {
                _request++; // A link still loading shouldn't land on top.
                Display(_back.Pop());
            }
        }
    }
}
