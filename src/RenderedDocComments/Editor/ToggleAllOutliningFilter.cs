using System;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using RenderedDocComments.Diagnostics;

namespace RenderedDocComments.Editor
{
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("CSharp")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ToggleAllOutliningFilterProvider : IVsTextViewCreationListener
    {
        [Import]
        internal IVsEditorAdaptersFactoryService Adapters { get; set; }

        [Import]
        internal EditorServices Services { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = Adapters.GetWpfTextView(textViewAdapter);
            var outlining = view == null ? null : Services.OutliningManagerService.GetOutliningManager(view);
            if (outlining == null)
            {
                return;
            }

            var filter = new ToggleAllOutliningFilter(view, outlining);
            if (textViewAdapter.AddCommandFilter(filter, out var next) == VSConstants.S_OK)
            {
                filter.Next = next;
            }
        }
    }

    /// <summary>
    /// Ctrl+M, L (Toggle All Outlining) expands everything if anything is collapsed, otherwise
    /// collapses everything. Cards are always collapsed folds (lines 2..N make room for the card),
    /// so VS always chose "expand all". This runs the same toggle with the cards' folds left out:
    /// they're neither counted nor changed, so the cards stay and everything else toggles as in
    /// plain VS (collapsing all folds doc comments, cards included, like Ctrl+M, O).
    /// </summary>
    internal sealed class ToggleAllOutliningFilter : IOleCommandTarget
    {
        private readonly IWpfTextView _view;
        private readonly IOutliningManager _outlining;

        public ToggleAllOutliningFilter(IWpfTextView view, IOutliningManager outlining)
        {
            _view = view;
            _outlining = outlining;
        }

        public IOleCommandTarget Next { get; set; }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) =>
            Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup == VSConstants.VSStd2K
                && nCmdID == (uint)VSConstants.VSStd2KCmdID.OUTLN_TOGGLE_ALL
                && _outlining.Enabled
                && Guard.Run("Toggling all outlining", ToggleAll, false))
            {
                return VSConstants.S_OK;
            }

            return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        /// <summary>The toggle, ignoring the cards' folds. False to let VS handle it (no cards here).</summary>
        private bool ToggleAll()
        {
            var snapshot = _view.TextBuffer.CurrentSnapshot;
            var all = new SnapshotSpan(snapshot, 0, snapshot.Length);
            var collapsed = _outlining.GetCollapsedRegions(all).ToList();
            if (!collapsed.Any(c => c.Tag is DocCommentRegionTag))
            {
                return false; // No cards: VS's own toggle is already right.
            }

            if (collapsed.Any(c => !(c.Tag is DocCommentRegionTag)))
            {
                _outlining.ExpandAll(all, c => !(c.Tag is DocCommentRegionTag));
            }
            else
            {
                _outlining.CollapseAll(all, c => !(c.Tag is DocCommentRegionTag));
            }

            _view.Caret.EnsureVisible();
            return true;
        }
    }
}
