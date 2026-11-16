using System;
using System.Diagnostics;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RenderedDocComments.Parsing;

namespace RenderedDocComments.Editor
{
    /// <summary>What clicking things in a rendered card does.</summary>
    internal static class DocCommentActions
    {
        public static void OpenUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            else
            {
                SetStatus("Not a web link: " + url);
            }
        }

        /// <summary>Show a message in VS's status bar (UI thread, like all editor code here).</summary>
        public static void SetStatus(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar statusBar)
            {
                statusBar.IsFrozen(out int frozen);
                if (frozen != 0)
                {
                    statusBar.FreezeOutput(0);
                }

                statusBar.SetText(text);
            }
        }
    }
}
