using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
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
    public sealed class ResourcePreviewPackage : AsyncPackage
    {
        public const string PackageGuidString = "69e55270-ef51-4488-9e95-cfd1a6852056";

        private static volatile ResourcePreviewPackage _instance;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // SettingsService switches to the UI thread itself where needed.
            await SettingsService.InitializeAsync(this, cancellationToken);

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
