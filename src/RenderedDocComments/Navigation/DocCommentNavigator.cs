using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;
using RenderedDocComments.Diagnostics;
using RenderedDocComments.Editor;
using RenderedDocComments.Parsing;
using RenderedDocComments.Rendering;

namespace RenderedDocComments.Navigation
{
    /// <summary>What a card's type and member links do: go to the definition on click, show the signature on hover.</summary>
    internal sealed class CardLinkHandler : ICardLinkHandler
    {
        private readonly ITextBuffer _buffer;
        private readonly ITrackingSpan _comment;

        public CardLinkHandler(ITextBuffer buffer, ITrackingSpan comment)
        {
            _buffer = buffer;
            _comment = comment;
        }

        public void GoToDefinition(string cref)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DocCommentNavigator.GoToDefinition(_buffer, _comment, cref);
        }

        public void Describe(string cref, Action<string> onDescribed)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DocCommentNavigator.Describe(_buffer, _comment, cref, onDescribed);
        }
    }

    internal static class DocCommentNavigator
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        private static JoinableTaskFactory Jtf => ThreadHelper.JoinableTaskContext.Factory;

        /// <summary>
        /// Resolve the cref with Roslyn and use VS's own Go To Definition, so source symbols open
        /// in their file and framework symbols open in the metadata / decompiled view.
        /// </summary>
        public static void GoToDefinition(ITextBuffer buffer, ITrackingSpan comment, string cref)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string display = CrefFormatter.Format(cref).ToString();
            var snapshot = buffer.CurrentSnapshot;
            var commentSpan = SpanIn(snapshot, comment);

            DocCommentActions.SetStatus("Going to " + display + "...");
            Jtf.RunAsync(async () =>
            {
                try
                {
                    using (var cancellation = new CancellationTokenSource(Timeout))
                    {
                        await TaskScheduler.Default;
                        var document = snapshot.GetOpenDocumentInCurrentContextWithChanges(); // Off the UI thread: it forks the solution.
                        if (document == null)
                        {
                            await Jtf.SwitchToMainThreadAsync(cancellation.Token);
                            DocCommentActions.SetStatus("Can't go to " + display + ": this file isn't part of a C# project or workspace.");
                            return;
                        }

                        var symbol = await CrefResolver.ResolveAsync(document, commentSpan, cref, cancellation.Token);

                        await Jtf.SwitchToMainThreadAsync(cancellation.Token);
                        if (symbol == null)
                        {
                            DocCommentActions.SetStatus("Couldn't find " + display + ". Check the cref compiles (no CS1574 warning).");
                            return;
                        }

                        var workspace = VisualStudioWorkspaceOrNull();
                        bool navigated = workspace != null
                            && await workspace.TryGoToDefinitionAsync(symbol, document.Project, cancellation.Token);

                        DocCommentActions.SetStatus(navigated ? string.Empty : "Couldn't open the definition of " + display + ".");
                    }
                }
                catch (OperationCanceledException)
                {
                    await Jtf.SwitchToMainThreadAsync();
                    DocCommentActions.SetStatus("Going to " + display + " took too long and was cancelled.");
                }
                catch (Exception ex) when (!Guard.IsCritical(ex))
                {
                    Guard.Report("Go To Definition", ex);
                    await Jtf.SwitchToMainThreadAsync();
                    DocCommentActions.SetStatus("Couldn't go to " + display + ".");
                }
            }).FileAndForget("RenderedDocComments/GoToDefinition");
        }

        /// <summary>VS's own Go To Definition for a symbol already found (from the quick documentation popup).</summary>
        public static void GoToSymbol(ISymbol symbol, Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string display = symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
            Jtf.RunAsync(async () =>
            {
                try
                {
                    using (var cancellation = new CancellationTokenSource(Timeout))
                    {
                        var workspace = VisualStudioWorkspaceOrNull();
                        bool navigated = workspace != null && await workspace.TryGoToDefinitionAsync(symbol, project, cancellation.Token);
                        await Jtf.SwitchToMainThreadAsync(cancellation.Token);
                        DocCommentActions.SetStatus(navigated ? string.Empty : "Couldn't open the definition of " + display + ".");
                    }
                }
                catch (OperationCanceledException)
                {
                    await Jtf.SwitchToMainThreadAsync();
                    DocCommentActions.SetStatus("Going to " + display + " took too long and was cancelled.");
                }
                catch (Exception ex) when (!Guard.IsCritical(ex))
                {
                    Guard.Report("Go To Definition", ex);
                    await Jtf.SwitchToMainThreadAsync();
                    DocCommentActions.SetStatus("Couldn't go to " + display + ".");
                }
            }).FileAndForget("RenderedDocComments/GoToSymbol");
        }

        /// <summary>Resolve the cref in the background and report its signature (for the link's tooltip).</summary>
        public static void Describe(ITextBuffer buffer, ITrackingSpan comment, string cref, Action<string> onDescribed)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var snapshot = buffer.CurrentSnapshot;
            var commentSpan = SpanIn(snapshot, comment);

            Jtf.RunAsync(async () =>
            {
                string description;
                try
                {
                    using (var cancellation = new CancellationTokenSource(Timeout))
                    {
                        await TaskScheduler.Default;
                        var document = snapshot.GetOpenDocumentInCurrentContextWithChanges(); // Off the UI thread: it forks the solution.
                        if (document == null)
                        {
                            return; // Not in a project: no signature to show.
                        }

                        var symbol = await CrefResolver.ResolveAsync(document, commentSpan, cref, cancellation.Token);
                        description = symbol == null ? cref + "  (not found)" : CrefResolver.Describe(symbol);
                    }
                }
                catch (Exception ex) when (!Guard.IsCritical(ex))
                {
                    if (!(ex is OperationCanceledException)) Guard.Report("Describing a link", ex);
                    description = cref;
                }

                await Jtf.SwitchToMainThreadAsync();
                onDescribed(description);
            }).FileAndForget("RenderedDocComments/DescribeCref");
        }

        private static TextSpan SpanIn(ITextSnapshot snapshot, ITrackingSpan comment)
        {
            var span = comment.GetSpan(snapshot);
            return new TextSpan(span.Start.Position, span.Length);
        }

        private static VisualStudioWorkspace VisualStudioWorkspaceOrNull()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            return componentModel?.GetService<VisualStudioWorkspace>();
        }
    }
}
