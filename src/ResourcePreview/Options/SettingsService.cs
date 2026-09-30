using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using ResourcePreview.Localization;
using Task = System.Threading.Tasks.Task;

namespace ResourcePreview.Options
{
    /// <summary>
    /// Service ID for Unified Settings. The real type lives in an internal VS interop assembly, so this
    /// empty interface only supplies the GUID for GetServiceAsync. Same approach as the SQLFluff SSMS extension.
    /// </summary>
    [Guid("e3684f31-344e-42ea-9047-b620fdc7ac25")]
    internal interface SVsUnifiedSettingsManager
    {
    }

    /// <summary>
    /// Single source of settings for the extension. Uses Unified Settings (Options\registration.json)
    /// when available, otherwise the legacy DialogPage ("classic mode"). The legacy page reads and
    /// writes through this class, so both dialogs show the same values.
    /// </summary>
    internal static class SettingsService
    {
        private static volatile ExtensionSettings _current = ExtensionSettings.Default;
        private static AsyncPackage _package;
        private static ISettingsManager _settingsManager;

        // Held so the change subscription is not garbage collected.
        private static IDisposable Subscription { get; set; }

        /// <summary>The current settings. Safe to read from any thread.</summary>
        public static ExtensionSettings Current => _current;

        /// <summary>Sets the settings directly without VS. For unit tests only.</summary>
        internal static void SetCurrentForTests(ExtensionSettings settings)
        {
            _current = settings ?? ExtensionSettings.Default;
        }

        /// <summary>True when Unified Settings is active and is the backing store.</summary>
        public static bool IsUnifiedSettingsActive { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package, CancellationToken cancellationToken)
        {
            _package = package;

            // Null when Unified Settings does not exist. Analyzer VSSDK006 requires the null check.
            var settingsManager = await package.GetServiceAsync(typeof(SVsUnifiedSettingsManager)) as ISettingsManager;
            _settingsManager = settingsManager;

            await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Reload();

            if (settingsManager != null)
            {
                // The callback thread is not guaranteed, so the handler switches to the UI thread.
                Subscription = settingsManager.GetReader().SubscribeToChanges(OnUnifiedSettingsChanged, SettingMonikers.All);
            }
        }

        private static void OnUnifiedSettingsChanged(SettingsUpdate update)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Reload();
            }).FileAndForget("ResourcePreview/SettingsChanged");
        }

        /// <summary>Rereads all settings and updates the language.</summary>
        public static void Reload()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ExtensionSettings settings = TryReadUnifiedSettings();
            if (settings == null)
            {
                IsUnifiedSettingsActive = false;
                settings = ((GeneralOptionsPage)_package.GetDialogPage(typeof(GeneralOptionsPage))).ToSettings();
            }
            else
            {
                IsUnifiedSettingsActive = true;
            }

            _current = settings;
            Localizer.Refresh(_package, settings.Language);
        }

        /// <summary>
        /// Writes values from the legacy options page to Unified Settings, keeping both dialogs in sync.
        /// </summary>
        public static void WriteToUnifiedSettings(ExtensionSettings settings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // The writer name appears in VS logging.
            ISettingsWriter writer = _settingsManager.GetWriter("XAML Resource Preview");
            writer.EnqueueChange(SettingMonikers.Language, LanguageInfo.ToSettingValue(settings.Language));
            writer.EnqueueChange(SettingMonikers.MaxListedDefinitions, settings.MaxListedDefinitions);
            writer.EnqueueChange(SettingMonikers.TileSize, settings.TileSize);
            writer.EnqueueChange(SettingMonikers.Tiles, ExtensionSettings.TilesToSetting(settings.Tiles));
            writer.EnqueueChange(SettingMonikers.LightForeground, ExtensionSettings.ColorToSetting(settings.LightForeground));
            writer.EnqueueChange(SettingMonikers.LightBackground, ExtensionSettings.ColorToSetting(settings.LightBackground));
            writer.EnqueueChange(SettingMonikers.DarkForeground, ExtensionSettings.ColorToSetting(settings.DarkForeground));
            writer.EnqueueChange(SettingMonikers.DarkBackground, ExtensionSettings.ColorToSetting(settings.DarkBackground));
            writer.EnqueueChange(SettingMonikers.ShowLiveSample, settings.ShowLiveSample);

            // RequestCommit may ask the user for approval first (PendingApproval). Commit() is obsolete.
            writer.RequestCommit("Changed on the legacy options page");
        }

        /// <summary>Returns null when Unified Settings is unavailable or off (classic mode).</summary>
        private static ExtensionSettings TryReadUnifiedSettings()
        {
            if (_settingsManager == null)
            {
                return null;
            }

            ISettingsReader reader = _settingsManager.GetReader();

            SettingRetrieval<string> language = reader.GetValue<string>(SettingMonikers.Language, SettingReadOptions.NoRequirements);
            if (language.Outcome == SettingRetrievalOutcome.NotSupportedInClassicMode)
            {
                return null;
            }

            ExtensionSettings defaults = ExtensionSettings.Default;
            return new ExtensionSettings(
                LanguageInfo.FromSettingValue(ValueOr(language, "automatic")),
                ValueOr(reader.GetValue<int>(SettingMonikers.MaxListedDefinitions, SettingReadOptions.NoRequirements), defaults.MaxListedDefinitions),
                ValueOr(reader.GetValue<int>(SettingMonikers.TileSize, SettingReadOptions.NoRequirements), defaults.TileSize),
                ExtensionSettings.TilesFromSetting(ValueOr(reader.GetValue<string>(SettingMonikers.Tiles, SettingReadOptions.NoRequirements), "both")),
                ReadColor(reader, SettingMonikers.LightForeground, defaults.LightForeground),
                ReadColor(reader, SettingMonikers.LightBackground, defaults.LightBackground),
                ReadColor(reader, SettingMonikers.DarkForeground, defaults.DarkForeground),
                ReadColor(reader, SettingMonikers.DarkBackground, defaults.DarkBackground),
                ValueOr(reader.GetValue<bool>(SettingMonikers.ShowLiveSample, SettingReadOptions.NoRequirements), defaults.ShowLiveSample));
        }

        private static System.Windows.Media.Color ReadColor(ISettingsReader reader, string moniker, System.Windows.Media.Color fallback)
        {
            SettingRetrieval<string> value = reader.GetValue<string>(moniker, SettingReadOptions.NoRequirements);
            return ExtensionSettings.ColorFromSetting(ValueOr(value, null), fallback);
        }

        // Any outcome other than Success falls back to the default.
        private static T ValueOr<T>(SettingRetrieval<T> retrieval, T fallback)
        {
            return retrieval.Outcome == SettingRetrievalOutcome.Success ? retrieval.Value : fallback;
        }
    }
}
