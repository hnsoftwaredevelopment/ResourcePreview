using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using ResourcePreview.Localization;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

namespace ResourcePreview.Options
{
    /// <summary>
    /// Options page for the legacy Options dialog and for VS versions without Unified Settings.
    /// Uses System.Drawing.Color because the PropertyGrid only shows a color picker for that type.
    /// When Unified Settings is active, values are read from and written to SettingsService.
    /// The GUID must match "legacyOptionPageId" in registration.json.
    /// </summary>
    [ComVisible(true)]
    [Guid("7C3B1D52-6E2A-4B9F-9C41-2F7B0E5A8D13")]
    public sealed class GeneralOptionsPage : DialogPage
    {
        public GeneralOptionsPage()
        {
            FromSettings(ExtensionSettings.Default);
        }

        [LocalizedCategory(Strings.OptionsCategoryGeneral)]
        [LocalizedDisplayName(Strings.OptionsLanguageName)]
        [LocalizedDescription(Strings.OptionsLanguageDescription)]
        [DefaultValue(ExtensionLanguage.Automatic)]
        public ExtensionLanguage Language { get; set; }

        [LocalizedCategory(Strings.OptionsCategoryGeneral)]
        [LocalizedDisplayName(Strings.OptionsMaxDefinitionsName)]
        [LocalizedDescription(Strings.OptionsMaxDefinitionsDescription)]
        [DefaultValue(5)]
        public int MaxListedDefinitions { get; set; }

        [LocalizedCategory(Strings.OptionsCategoryAppearance)]
        [LocalizedDisplayName(Strings.OptionsTileSizeName)]
        [LocalizedDescription(Strings.OptionsTileSizeDescription)]
        [DefaultValue(64)]
        public int TileSize { get; set; }

        [LocalizedCategory(Strings.OptionsCategoryAppearance)]
        [LocalizedDisplayName(Strings.OptionsTilesName)]
        [LocalizedDescription(Strings.OptionsTilesDescription)]
        [DefaultValue(PreviewTiles.Both)]
        public PreviewTiles Tiles { get; set; }

        [LocalizedCategory(Strings.OptionsCategoryAppearance)]
        [LocalizedDisplayName(Strings.OptionsLightForegroundName)]
        [LocalizedDescription(Strings.OptionsForegroundDescription)]
        public DrawingColor LightTileForeground { get; set; }

        [LocalizedCategory(Strings.OptionsCategoryAppearance)]
        [LocalizedDisplayName(Strings.OptionsLightBackgroundName)]
        [LocalizedDescription(Strings.OptionsBackgroundDescription)]
        public DrawingColor LightTileBackground { get; set; }

        [LocalizedCategory(Strings.OptionsCategoryAppearance)]
        [LocalizedDisplayName(Strings.OptionsDarkForegroundName)]
        [LocalizedDescription(Strings.OptionsForegroundDescription)]
        public DrawingColor DarkTileForeground { get; set; }

        [LocalizedCategory(Strings.OptionsCategoryAppearance)]
        [LocalizedDisplayName(Strings.OptionsDarkBackgroundName)]
        [LocalizedDescription(Strings.OptionsBackgroundDescription)]
        public DrawingColor DarkTileBackground { get; set; }

        [LocalizedCategory(Strings.OptionsCategoryAppearance)]
        [LocalizedDisplayName(Strings.OptionsLiveSampleName)]
        [LocalizedDescription(Strings.OptionsLiveSampleDescription)]
        [DefaultValue(true)]
        public bool ShowLiveSample { get; set; }

        internal ExtensionSettings ToSettings()
        {
            return new ExtensionSettings(
                Language, MaxListedDefinitions, TileSize, Tiles,
                ToMedia(LightTileForeground), ToMedia(LightTileBackground),
                ToMedia(DarkTileForeground), ToMedia(DarkTileBackground),
                ShowLiveSample);
        }

        private void FromSettings(ExtensionSettings settings)
        {
            Language = settings.Language;
            MaxListedDefinitions = settings.MaxListedDefinitions;
            TileSize = settings.TileSize;
            Tiles = settings.Tiles;
            LightTileForeground = ToDrawing(settings.LightForeground);
            LightTileBackground = ToDrawing(settings.LightBackground);
            DarkTileForeground = ToDrawing(settings.DarkForeground);
            DarkTileBackground = ToDrawing(settings.DarkBackground);
            ShowLiveSample = settings.ShowLiveSample;
        }

        /// <summary>Refreshes the values from Unified Settings when the page opens.</summary>
        protected override void OnActivate(CancelEventArgs e)
        {
            if (SettingsService.IsUnifiedSettingsActive)
            {
                FromSettings(SettingsService.Current);
            }

            base.OnActivate(e);
        }

        public override void LoadSettingsFromStorage()
        {
            base.LoadSettingsFromStorage();

            if (SettingsService.IsUnifiedSettingsActive)
            {
                FromSettings(SettingsService.Current);
            }
        }

        /// <summary>Called after OK. Writes to Unified Settings when active, otherwise to the DialogPage store.</summary>
        public override void SaveSettingsToStorage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (SettingsService.IsUnifiedSettingsActive)
            {
                SettingsService.WriteToUnifiedSettings(ToSettings());
            }
            else
            {
                base.SaveSettingsToStorage();
            }

            SettingsService.Reload();
        }

        private static MediaColor ToMedia(DrawingColor color) => MediaColor.FromArgb(color.A, color.R, color.G, color.B);

        private static DrawingColor ToDrawing(MediaColor color) => DrawingColor.FromArgb(color.A, color.R, color.G, color.B);
    }
}
