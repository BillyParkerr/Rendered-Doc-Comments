using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using System.Windows.Threading;
using Microsoft.VisualStudio.Threading;
using RenderedDocComments.Diagnostics;
using RenderedDocComments.Parsing;

namespace RenderedDocComments.Navigation
{
    /// <summary>
    /// Inherited documentation for the &lt;inheritdoc/&gt; comments in one file. Lookups run in the
    /// background; until one finishes, the last known result is returned so cards don't flicker
    /// while you type. Any edit can change what a comment inherits (the base class may be in this
    /// file), but looking it up again means compiling the file, so that waits until typing pauses.
    /// Edits in another file that the docs came from (a base class elsewhere) count too.
    /// </summary>
    internal sealed class InheritedDocStore
    {
        private const int MaxEntries = 256;
        private static readonly TimeSpan RefreshAfterTypingPause = TimeSpan.FromMilliseconds(600);

        private readonly ITextBuffer _buffer;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly DispatcherTimer _refreshTimer;

        /// <summary>Files the current inherited docs came from (read from any thread; replaced, never changed).</summary>
        private volatile HashSet<string> _sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every store, for edits in other files; weak, so closing a file's editors releases its store.</summary>
        private static readonly List<WeakReference<InheritedDocStore>> Stores = new List<WeakReference<InheritedDocStore>>();
        private static bool _watchingWorkspace;

        private InheritedDocStore(ITextBuffer buffer)
        {
            ThreadHelper.ThrowIfNotOnUIThread(); // Created by editor code, for the editor's buffer.
            _buffer = buffer;
            _refreshTimer = new DispatcherTimer(
                RefreshAfterTypingPause,
                DispatcherPriority.Background,
                OnRefreshTimer,
                Dispatcher.FromThread(ThreadHelper.JoinableTaskContext.MainThread) ?? Dispatcher.CurrentDispatcher);
            _refreshTimer.Stop();

            _buffer.Changed += (s, e) =>
            {
                if (_entries.Count == 0) return;
                RestartRefreshTimer();
            };

            Register(this);
        }

        /// <summary>Refresh after the pause; another edit before then restarts the wait.</summary>
        private void RestartRefreshTimer()
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        private static void Register(InheritedDocStore store)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            lock (Stores)
            {
                Stores.RemoveAll(s => !s.TryGetTarget(out _));
                Stores.Add(new WeakReference<InheritedDocStore>(store));
            }

            if (_watchingWorkspace)
            {
                return;
            }

            _watchingWorkspace = true;
            var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            var workspace = componentModel?.GetService<VisualStudioWorkspace>();
            if (workspace != null)
            {
                workspace.WorkspaceChanged += OnWorkspaceChanged;
            }
        }

        /// <summary>
        /// Raised for every document change in the solution, on a background thread. Cheap unless
        /// a store's inherited docs came from the changed file; that store then refreshes after the
        /// usual pause (on the UI thread).
        /// </summary>
        private static void OnWorkspaceChanged(object sender, WorkspaceChangeEventArgs e)
        {
            if (e.Kind != WorkspaceChangeKind.DocumentChanged && e.Kind != WorkspaceChangeKind.DocumentReloaded)
            {
                return;
            }

            string file = e.DocumentId == null ? null : e.NewSolution.GetDocument(e.DocumentId)?.FilePath;
            if (string.IsNullOrEmpty(file))
            {
                return;
            }

            var affected = new List<InheritedDocStore>();
            lock (Stores)
            {
                foreach (var weak in Stores)
                {
                    if (weak.TryGetTarget(out var store) && store._sourceFiles.Contains(file)) affected.Add(store);
                }
            }

            foreach (var store in affected)
            {
                // Guard.Later only queues the work onto the UI thread's dispatcher: safe from here.
#pragma warning disable VSTHRD010
                Guard.Later(store._refreshTimer.Dispatcher, DispatcherPriority.Background, "Refreshing inherited docs", store.RestartRefreshTimer);
#pragma warning restore VSTHRD010
            }
        }

        private void UpdateSourceFiles()
        {
            _sourceFiles = new HashSet<string>(
                _entries.Values.Where(e => e.Value != null).SelectMany(e => e.Value.SourceFiles),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Raised on the UI thread when a lookup finishes with a different result, and when typing
        /// pauses (cards then ask again, which starts the lookups).
        /// </summary>
        public event EventHandler Changed;

        private static JoinableTaskFactory Jtf => ThreadHelper.JoinableTaskContext.Factory;

        public static InheritedDocStore Get(ITextBuffer buffer) =>
            buffer.Properties.GetOrCreateSingletonProperty(() => new InheritedDocStore(buffer));

        public static bool UsesInheritDoc(DocCommentBlock block) =>
            block.Xml.IndexOf("inheritdoc", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The last known inherited docs for this comment (null before the first lookup finishes).</summary>
        public InheritedDoc GetOrRequest(DocCommentBlock block)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string key = KeyFor(block);
            if (!_entries.TryGetValue(key, out var entry))
            {
                if (_entries.Count >= MaxEntries)
                {
                    foreach (var old in _entries.Where(e => !e.Value.Running).Select(e => e.Key).ToList()) _entries.Remove(old);
                }

                // Same comment with its declaration being edited: show what it inherited so far.
                entry = new Entry { Stale = true, Value = LastKnownFor(block) };
                _entries[key] = entry;
            }

            // While typing, look up only what has nothing to show yet; the rest waits for the pause.
            bool typing = _refreshTimer.IsEnabled;
            if (entry.Stale && !entry.Running && (entry.Value == null || !typing))
            {
                Start(entry, block);
            }

            return entry.Value;
        }

        private InheritedDoc LastKnownFor(DocCommentBlock block)
        {
            string prefix = block.Xml + "\n";
            foreach (var pair in _entries)
            {
                if (pair.Value.Value != null && pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return pair.Value.Value;
                }
            }

            return null;
        }

        /// <summary>The timer ticks on the UI thread (its dispatcher is the main thread's).</summary>
        private void OnRefreshTimer(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Guard.Run("Refreshing inherited docs", RefreshAll);
        }

        private void RefreshAll()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _refreshTimer.Stop();
            foreach (var entry in _entries.Values) entry.Stale = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The comment's text plus the declaration below it: the same &lt;inheritdoc/&gt; on two
        /// members inherits different things, while edits elsewhere in the file don't change the key.
        /// </summary>
        private static string KeyFor(DocCommentBlock block)
        {
            var snapshot = block.Snapshot;
            string declaration = block.EndLine + 1 < snapshot.LineCount
                ? snapshot.GetLineFromLineNumber(block.EndLine + 1).GetText().Trim()
                : string.Empty;
            return block.Xml + "\n" + declaration;
        }

        private void Start(Entry entry, DocCommentBlock block)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var snapshot = _buffer.CurrentSnapshot;
            var span = block.Span.TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive);
            var commentSpan = new TextSpan(span.Start.Position, span.Length);
            entry.Stale = false;
            entry.Running = true;

            Jtf.RunAsync(async () =>
            {
                InheritedDoc result;
                try
                {
                    using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    {
                        await TaskScheduler.Default;

                        // Getting Roslyn's document for the edited text forks the solution, which
                        // takes long enough to stall the editor, so it's done here, off the UI thread.
                        var document = snapshot.GetOpenDocumentInCurrentContextWithChanges();
                        result = document == null
                            ? InheritedDoc.NotFound
                            : await InheritDocResolver.ResolveAsync(document, commentSpan, cancellation.Token);
                    }
                }
                catch (Exception)
                {
                    result = entry.Value ?? InheritedDoc.NotFound; // Keep what we had.
                }

                await Jtf.SwitchToMainThreadAsync();
                entry.Running = false;
                // Keep the same result object when nothing changed: cards are cached against it,
                // and a new object (even with the same docs) would rebuild the card.
                bool changed = entry.Value == null || entry.Value.Xml != result.Xml || entry.Value.SourceId != result.SourceId;
                if (changed)
                {
                    entry.Value = result;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                else if (!entry.Value.SourceFiles.OrderBy(f => f).SequenceEqual(result.SourceFiles.OrderBy(f => f), StringComparer.OrdinalIgnoreCase))
                {
                    entry.Value = result; // Same docs, different files to watch (e.g. a base class moved).
                }

                UpdateSourceFiles();
            }).FileAndForget("RenderedDocComments/InheritDoc");
        }

        private sealed class Entry
        {
            public InheritedDoc Value { get; set; }
            public bool Stale { get; set; }
            public bool Running { get; set; }
        }
    }
}
