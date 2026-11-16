using System;
using System.Windows.Controls;
using Microsoft.VisualStudio.Text.Classification;

// Just enough of the extension's VS-facing types for the card renderer to run outside Visual
// Studio. With no editor services, DocTheme falls back to its built-in light colours.
namespace RenderedDocComments.Editor
{
    internal sealed class EditorServices
    {
        public static EditorServices Instance => null;

        public IEditorFormatMapService FormatMapService => null;

        public IClassificationFormatMapService ClassificationFormatMapService => null;

        public IClassificationTypeRegistryService ClassificationRegistry => null;
    }

    internal static class DocCommentActions
    {
        public static void OpenUrl(string url)
        {
        }

        public static void SetStatus(string text)
        {
        }
    }

    internal static class CardTextSelection
    {
        public static void Register(RichTextBox box)
        {
        }
    }
}

namespace RenderedDocComments.Options
{
    internal static class ExtensionOptions
    {
        public const string DefaultProseFontFamily = "Segoe UI Variable Text, Segoe UI";

        public static event EventHandler Changed
        {
            add { }
            remove { }
        }

        public static int MaxCardWidth => 760;

        public static string ProseFontFamily => DefaultProseFontFamily;

        public static int ProseFontSizePercent => 100;

        public static void EnsureLoaded()
        {
        }
    }
}

namespace RenderedDocComments.Diagnostics
{
    internal static class Guard
    {
        public static void Run(string operation, Action action) => action();

        public static T Run<T>(string operation, Func<T> func, T fallback) => func();
    }
}
