using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using ResourcePreview.Diagnostics;
using ResourcePreview.Navigation;
using ResourcePreview.Options;
using Task = System.Threading.Tasks.Task;

namespace ResourcePreview
{
    /// <summary>
    /// Package that hosts the options page and applies the language setting.
    /// The MEF components do not need it. Never change the package GUID.
    /// 100 and 101 are resource IDs in VSPackage.resx. IsInUnifiedSettings keeps the legacy page
    /// out of the new Options dialog, where registration.json replaces it.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideOptionPage(
        typeof(GeneralOptionsPage), "XAML Resource Preview", "General", 100, 101, true,
        IsInUnifiedSettings = true,
        UnifiedSettingsCategoryMoniker = "resourcePreview.general")]
    [ProvideSettingsManifest(PackageRelativeManifestFile = @"Options\registration.json")]
    // Raise the version after every change to the .vsct, otherwise VS keeps its cached menus and key bindings.
    [ProvideMenuResource("Menus.ctmenu", 4)]
    // The list of references opens as a tab next to the Output window the first time.
    [ProvideToolWindow(typeof(ResourceReferencesWindow), Style = VsDockStyle.Tabbed, Window = "34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3")]
    // Load in the background as soon as a .xaml file is active, so the context menu command
    // can decide whether to show itself. Without this the hidden menu item never appears.
    [ProvideAutoLoad(XamlFileContextGuidString, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideUIContextRule(
        XamlFileContextGuidString,
        name: "XAML file active",
        expression: "XamlEditor | XamlFile",
        termNames: new[] { "XamlEditor", "XamlFile" },
        termValues: new[] { "ActiveEditorContentType:XAML", "HierSingleSelectionName:.xaml$" })]
    public sealed class ResourcePreviewPackage : AsyncPackage
    {
        public const string PackageGuidString = "69e55270-ef51-4488-9e95-cfd1a6852056";
        public const string XamlFileContextGuidString = "7215ba6b-8c21-48f9-bf17-536b03594ba2";

        private static volatile ResourcePreviewPackage _instance;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // SettingsService switches to the UI thread itself where needed.
            await SettingsService.InitializeAsync(this, cancellationToken);
            await GoToResourceDefinitionCommand.InitializeAsync(this);
            await FindResourceReferencesCommand.InitializeAsync(this);
            await RenameResourceKeyCommand.InitializeAsync(this);
            await ResourceErrorList.InitializeAsync(this);

            _instance = this;
        }

        /// <summary>
        /// Loads the package so the language is set before any text is shown.
        /// VS does not load it for MEF components, so the first Quick Info hover triggers it.
        /// </summary>
        internal static async Task EnsureLoadedAsync(JoinableTaskFactory joinableTaskFactory, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            if (_instance != null)
            {
                return;
            }

            await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (_instance == null && serviceProvider.GetService(typeof(SVsShell)) is IVsShell7 shell)
            {
                Guid packageGuid = new Guid(PackageGuidString);
                await shell.LoadPackageAsync(ref packageGuid);
            }
        }
    }
}
