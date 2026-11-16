using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using RenderedDocComments.Commands;
using RenderedDocComments.Options;

namespace RenderedDocComments
{
    /// <summary>
    /// Hosts the menu commands. The editor features themselves are MEF components and load
    /// with the C# editor; this package only loads when one of its commands is used.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    // Raise the version whenever the .vsct changes: VS only reloads commands and key bindings when it does.
    [ProvideMenuResource("Menus.ctmenu", 2)]
    // The settings, for Visual Studio 2026's Settings window. The classic Tools > Options page is
    // hidden wherever that window is used (IsInUnifiedSettings), and shown otherwise.
    [ProvideSettingsManifest(PackageRelativeManifestFile = @"Options\registration.json")]
    [ProvideOptionPage(typeof(OptionsPage), "Rendered Doc Comments", "General", 0, 0, true, IsInUnifiedSettings = true)]
    [Guid(PackageGuidString)]
    public sealed class RenderedDocCommentsPackage : AsyncPackage
    {
        public const string PackageGuidString = "4fe8fd1a-ded2-4538-a087-90952ef3ee1a";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            var textManager = await GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;

            if (commandService != null && componentModel != null && textManager != null)
            {
                DocCommentCommands.Register(commandService, componentModel, textManager);
            }
        }
    }
}
