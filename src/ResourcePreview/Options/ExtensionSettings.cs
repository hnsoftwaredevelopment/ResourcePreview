using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using ResourcePreview.Localization;

namespace ResourcePreview.Options
{
    /// <summary>
    /// Which background tiles the preview shows. Not named TileMode to avoid a clash with
    /// System.Windows.Media.TileMode.
    /// </summary>
    [TypeConverter(typeof(PreviewTilesConverter))]
    public enum PreviewTiles
    {
        Both,
        Light,
        Dark,
    }

    /// <summary>
    /// Setting monikers from registration.json. The part before the last dot must be a
    /// category defined there.
    /// </summary>
    internal static class SettingMonikers
    {
        public const string Language = "resourcePreview.general.language";
        public const string MaxListedDefinitions = "resourcePreview.general.maxListedDefinitions";
        public const string TileSize = "resourcePreview.appearance.tileSize";
        public const string Tiles = "resourcePreview.appearance.tiles";
        public const string LightForeground = "resourcePreview.appearance.lightTileForeground";
        public const string LightBackground = "resourcePreview.appearance.lightTileBackground";
        public const string DarkForeground = "resourcePreview.appearance.darkTileForeground";
        public const string DarkBackground = "resourcePreview.appearance.darkTileBackground";
        public const string ShowLiveSample = "resourcePreview.appearance.showLiveSample";
        public const string WarnOverriddenKeys = "resourcePreview.general.warnOverriddenKeys";
        public const string ShowInCompletion = "resourcePreview.general.showInCompletion";

        public static readonly string[] All =
        {
            Language, MaxListedDefinitions, TileSize, Tiles, LightForeground, LightBackground, DarkForeground, DarkBackground, ShowLiveSample, WarnOverriddenKeys, ShowInCompletion,
        };
    }

    /// <summary>
    /// Immutable snapshot of all settings, so any thread can read <see cref="SettingsService.Current"/>
    /// without locks.
    /// </summary>
    internal sealed class ExtensionSettings
    {
        public const int MinTileSize = 16;
        public const int MaxTileSize = 256;
        public const int MinListedDefinitions = 0;
        public const int MaxListedDefinitionsLimit = 50;

        public static readonly ExtensionSettings Default = new ExtensionSettings(
            ExtensionLanguage.Automatic, 5, 64, PreviewTiles.Both,
            Colors.Black, Colors.White, Colors.White, Color.FromRgb(0x1E, 0x1E, 0x1E), showLiveSample: true, warnOverriddenKeys: true, showInCompletion: true);

        public ExtensionSettings(
            ExtensionLanguage language,
            int maxListedDefinitions,
            int tileSize,
            PreviewTiles tiles,
            Color lightForeground,
            Color lightBackground,
            Color darkForeground,
            Color darkBackground,
            bool showLiveSample = true,
            bool warnOverriddenKeys = true,
            bool showInCompletion = true)
        {
            Language = language;
            MaxListedDefinitions = Clamp(maxListedDefinitions, MinListedDefinitions, MaxListedDefinitionsLimit);
            TileSize = Clamp(tileSize, MinTileSize, MaxTileSize);
            Tiles = tiles;
            LightForeground = lightForeground;
            LightBackground = lightBackground;
            DarkForeground = darkForeground;
            DarkBackground = darkBackground;
            ShowLiveSample = showLiveSample;
            WarnOverriddenKeys = warnOverriddenKeys;
            ShowInCompletion = showInCompletion;
        }

        public ExtensionLanguage Language { get; }
        public int MaxListedDefinitions { get; }
        public int TileSize { get; }
        public PreviewTiles Tiles { get; }
        public Color LightForeground { get; }
        public Color LightBackground { get; }
        public Color DarkForeground { get; }
        public Color DarkBackground { get; }

        /// <summary>Shows a live control with the style or template applied, above the setter table or instead of the tree.</summary>
        public bool ShowLiveSample { get; }

        /// <summary>Lists resource definitions that WPF never uses in the Error List.</summary>
        public bool WarnOverriddenKeys { get; }

        /// <summary>Icons and the preview tooltip in the IntelliSense list after {StaticResource.</summary>
        public bool ShowInCompletion { get; }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

        // --- Conversion to and from the string values in registration.json ---

        public static string TilesToSetting(PreviewTiles mode)
        {
            switch (mode)
            {
                case PreviewTiles.Light: return "light";
                case PreviewTiles.Dark: return "dark";
                default: return "both";
            }
        }

        public static PreviewTiles TilesFromSetting(string value)
        {
            switch (value)
            {
                case "light": return PreviewTiles.Light;
                case "dark": return PreviewTiles.Dark;
                default: return PreviewTiles.Both;
            }
        }

        /// <summary>Returns "#RRGGBB" or "#AARRGGBB", the forms the pattern in registration.json accepts.</summary>
        public static string ColorToSetting(Color color)
        {
            return color.A == 0xFF
                ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        /// <summary>Accepts "#RRGGBB", "#AARRGGBB" and WPF color names such as "Red". Returns the fallback when invalid.</summary>
        public static Color ColorFromSetting(string value, Color fallback)
        {
            return ResourcePreview.Resources.ColorParser.TryParse(value, out Color color) ? color : fallback;
        }
    }

    /// <summary>Shows localized tile names on the legacy options page.</summary>
    internal sealed class PreviewTilesConverter : EnumConverter
    {
        public PreviewTilesConverter()
            : base(typeof(PreviewTiles))
        {
        }

        private static string GetDisplayName(PreviewTiles mode)
        {
            switch (mode)
            {
                case PreviewTiles.Light: return Localizer.Get(Strings.TilesLight);
                case PreviewTiles.Dark: return Localizer.Get(Strings.TilesDark);
                default: return Localizer.Get(Strings.TilesBoth);
            }
        }

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
        {
            if (destinationType == typeof(string) && value is PreviewTiles mode)
            {
                return GetDisplayName(mode);
            }

            return base.ConvertTo(context, culture, value, destinationType);
        }

        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
        {
            if (value is string text)
            {
                foreach (PreviewTiles mode in Enum.GetValues(typeof(PreviewTiles)))
                {
                    if (text == GetDisplayName(mode) || string.Equals(text, mode.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        return mode;
                    }
                }

                return PreviewTiles.Both;
            }

            return base.ConvertFrom(context, culture, value);
        }
    }
}
