using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using RenderedDocComments.Diagnostics;

namespace RenderedDocComments.Options
{
    /// <summary>
    /// The extension's settings in Visual Studio 2026's Settings window (Unified Settings), as
    /// registered in Options\registration.json: reading them, writing the shortcut back, and
    /// hearing about changes. Where Visual Studio has no Unified Settings, <see cref="TryCreate"/>
    /// returns null and the extension uses its classic settings store and Options page instead.
    /// </summary>
    internal sealed class UnifiedSettingsStore
    {
        public const string RenderByDefault = "renderedDocComments.renderByDefault";
        public const string ShowGutterIcon = "renderedDocComments.showGutterIcon";
        public const string MaxCardWidth = "renderedDocComments.maxCardWidth";
        public const string TextFont = "renderedDocComments.textFont";
        public const string TextSizePercent = "renderedDocComments.textSizePercent";
        public const string ShowCardsOnHover = "renderedDocComments.showCardsOnHover";
        public const string QuickDocShortcut = "renderedDocComments.quickDocumentationShortcut";

        private static readonly string[] All =
        {
            RenderByDefault, ShowGutterIcon, MaxCardWidth, TextFont, TextSizePercent, ShowCardsOnHover, QuickDocShortcut,
        };

        private readonly ISettingsManager _manager;
        private readonly ISettingsReader _reader;

        private UnifiedSettingsStore(ISettingsManager manager)
        {
            _manager = manager;
            _reader = manager.GetReader();
        }

        /// <summary>The store, or null where Visual Studio has no Unified Settings.</summary>
        public static UnifiedSettingsStore TryCreate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return Create();
            }
            catch (Exception ex) when (!Guard.IsCritical(ex))
            {
                return null; // Including older Visual Studio versions without these types.
            }
        }

        // Separate, so a Visual Studio without the Unified Settings types fails here (when this
        // method is compiled) and TryCreate can catch it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static UnifiedSettingsStore Create()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Package.GetGlobalService(typeof(SVsUnifiedSettingsManager)) is ISettingsManager manager
                ? new UnifiedSettingsStore(manager)
                : null;
        }

        /// <summary>A setting's value; false if Unified Settings couldn't provide one.</summary>
        public bool TryGet<T>(string moniker, out T value)
        {
            var retrieval = _reader.GetValue<T>(moniker);
            value = retrieval.Value;
            return retrieval.Outcome == SettingRetrievalOutcome.Success;
        }

        /// <summary>Change a setting, as the Settings window would.</summary>
        public void Set(string moniker, string value)
        {
            var writer = _manager.GetWriter("RenderedDocComments");
            writer.EnqueueChange(moniker, value);
            writer.RequestCommit("Rendered Doc Comments: the quick documentation shortcut was changed in the keyboard settings");
        }

        /// <summary>Calls <paramref name="changed"/>, on any thread, when any of the extension's settings change.</summary>
        public IDisposable Subscribe(Action<SettingsUpdate> changed) => _reader.SubscribeToChanges(changed, All);

        /// <summary>
        /// The service that provides <see cref="ISettingsManager"/>. Visual Studio declares it in an
        /// interop assembly that isn't part of the SDK's references; a service is looked up by
        /// the GUID alone, so this declares the same one.
        /// </summary>
        [Guid("e3684f31-344e-42ea-9047-b620fdc7ac25")]
        private interface SVsUnifiedSettingsManager
        {
        }
    }
}
