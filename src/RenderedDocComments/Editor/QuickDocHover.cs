using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using RenderedDocComments.Diagnostics;
using RenderedDocComments.Navigation;
using RenderedDocComments.Options;

namespace RenderedDocComments.Editor
{
    /// <summary>
    /// With "Show cards on hover" on, hovering a name shows the quick documentation popup instead
    /// of VS's tooltip (Quick Info). This takes part in each hover's Quick Info session: when the
    /// mouse is over a name and not over an error or warning, it closes the session before VS
    /// shows anything and opens the popup. Otherwise (and for Ctrl+K, Ctrl+I) VS's tooltip shows.
    /// </summary>
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("RenderedDocComments.QuickDocHover")]
    [ContentType("CSharp")]
    internal sealed class QuickDocHoverSourceProvider : IAsyncQuickInfoSourceProvider
    {
        [Import]
        internal IViewTagAggregatorFactoryService TagAggregators { get; set; }

        [Import]
        internal EditorServices Services { get; set; }

        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer) =>
            textBuffer.Properties.GetOrCreateSingletonProperty(() => new QuickDocHoverSource(textBuffer, TagAggregators));
    }

    internal sealed class QuickDocHoverSource : IAsyncQuickInfoSource
    {
        private readonly ITextBuffer _buffer;
        private readonly IViewTagAggregatorFactoryService _tagAggregators;

        public QuickDocHoverSource(ITextBuffer buffer, IViewTagAggregatorFactoryService tagAggregators)
        {
            _buffer = buffer;
            _tagAggregators = tagAggregators;
        }

        private static JoinableTaskFactory Jtf => ThreadHelper.JoinableTaskContext.Factory;

        public async Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            if ((session.Options & QuickInfoSessionOptions.TrackMouse) == 0 // Ctrl+K, Ctrl+I: VS's own.
                || !(session.TextView is IWpfTextView view))
            {
                return null;
            }

            try
            {
                var point = session.GetTriggerPoint(_buffer.CurrentSnapshot);
                if (point == null)
                {
                    return null;
                }

                await Jtf.SwitchToMainThreadAsync(cancellationToken);
                ExtensionOptions.EnsureLoaded(); // Settings load on the UI thread; no card may have loaded them yet.
                if (!ExtensionOptions.ShowCardsOnHover || HasSquiggleAt(view, point.Value))
                {
                    return null; // VS's tooltip shows the error or warning.
                }

                // A popup opened with the shortcut, or clicked in, stays put: the mouse passing
                // over other names on its way there neither replaces it nor covers it with a tooltip.
                if (QuickDocPopup.TryGet(view)?.IsPinned == true)
                {
                    await session.DismissAsync();
                    return null;
                }

                var snapshot = point.Value.Snapshot;
                await TaskScheduler.Default;
                var document = snapshot.GetOpenDocumentInCurrentContextWithChanges(); // Off the UI thread: it forks the solution.
                var target = document == null ? null : await QuickDocResolver.AtPositionAsync(document, point.Value.Position, cancellationToken);
                if (target == null)
                {
                    return null; // Not a name: whatever VS shows (if anything) stays.
                }

                await Jtf.SwitchToMainThreadAsync(cancellationToken);
                if (session.State == QuickInfoSessionState.Dismissed || view.IsClosed)
                {
                    return null;
                }

                // Closing the session also cancels this lookup's token; nothing below uses it.
                await session.DismissAsync();
                Guard.Run("Showing quick documentation", () =>
                    QuickDocPopup.For(view).Show(document, target, new SnapshotSpan(snapshot, target.Span.Start, target.Span.Length), hover: true));
            }
            catch (OperationCanceledException)
            {
                // The mouse moved on.
            }
            catch (Exception ex) when (!Guard.IsCritical(ex))
            {
                Guard.Report("Quick documentation on hover", ex);
            }

            return null;
        }

        /// <summary>An error, warning or suggestion under the mouse (VS's tooltip explains it).</summary>
        private bool HasSquiggleAt(IWpfTextView view, SnapshotPoint point)
        {
            var aggregator = view.Properties.GetOrCreateSingletonProperty(typeof(QuickDocHoverSource), () =>
            {
                var created = _tagAggregators.CreateTagAggregator<IErrorTag>(view);
                view.Closed += (s, e) => created.Dispose();
                return created;
            });

            var span = new SnapshotSpan(point, point.Position < point.Snapshot.Length ? 1 : 0);
            return aggregator.GetTags(span).Any();
        }

        public void Dispose()
        {
        }
    }
}
