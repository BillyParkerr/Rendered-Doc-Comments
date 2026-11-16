using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using RenderedDocComments.Editor;

namespace RenderedDocComments.Diagnostics
{
    /// <summary>
    /// Runs extension code that VS calls into (layout, taggers, event handlers, commands) so a
    /// bug can never break the editor: the error is logged once to VS's activity log, and the
    /// caller falls back to plain behaviour (usually: the comment shows raw).
    /// </summary>
    internal static class Guard
    {
        private const string Source = "RenderedDocComments";

        private static readonly HashSet<string> Reported = new HashSet<string>();
        private static int _toldUser;

        public static void Run(string operation, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (!IsCritical(ex))
            {
                Report(operation, ex);
            }
        }

        public static T Run<T>(string operation, Func<T> func, T fallback)
        {
            try
            {
                return func();
            }
            catch (Exception ex) when (!IsCritical(ex))
            {
                Report(operation, ex);
                return fallback;
            }
        }

        /// <summary>Dispatcher.BeginInvoke, with the queued work guarded too.</summary>
        public static void Later(Dispatcher dispatcher, DispatcherPriority priority, string operation, Action action) =>
            dispatcher.BeginInvoke(priority, new Action(() => Run(operation, action)));

        /// <summary>Errors that mean the process is in trouble; never swallowed.</summary>
        public static bool IsCritical(Exception ex) =>
            ex is OutOfMemoryException || ex is StackOverflowException || ex is AccessViolationException || ex is ThreadAbortException;

        public static void Report(string operation, Exception ex)
        {
            // Layout runs constantly: log each distinct failure once, not on every frame.
            string key = operation + "|" + ex.GetType().FullName + "|" + ex.TargetSite?.DeclaringType?.FullName + "." + ex.TargetSite?.Name;
            lock (Reported)
            {
                if (!Reported.Add(key)) return;
            }

            try
            {
                ActivityLog.LogError(Source, operation + " failed: " + ex);
            }
            catch (Exception)
            {
                // Nowhere left to report to.
            }

            if (ThreadHelper.CheckAccess() && Interlocked.Exchange(ref _toldUser, 1) == 0)
            {
                try
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    DocCommentActions.SetStatus("Rendered Doc Comments hit an error (" + operation + "). Affected comments show their plain text; details are in VS's activity log.");
                }
                catch (Exception)
                {
                    // Status bar unavailable.
                }
            }
        }
    }
}
