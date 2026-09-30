using System;
using System.Globalization;
using System.Reflection;
using System.Windows.Media;

namespace ResourcePreview.Resources
{
    /// <summary>
    /// Parses "#RGB", "#ARGB", "#RRGGBB", "#AARRGGBB" or a named color like "Red" without throwing.
    /// Avoids ColorConverter.ConvertFromString, which throws FormatException on invalid input.
    /// Safe to call from any thread.
    /// </summary>
    internal static class ColorParser
    {
        public static bool TryParse(string text, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();
            if (text.StartsWith("#", StringComparison.Ordinal))
            {
                return TryParseHex(text.Substring(1), out color);
            }

            // Named colors are case-insensitive, as in XAML.
            PropertyInfo named = typeof(Colors).GetProperty(text, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
            if (named != null && named.PropertyType == typeof(Color))
            {
                color = (Color)named.GetValue(null);
                return true;
            }

            return false;
        }

        private static bool TryParseHex(string hex, out Color color)
        {
            color = default;
            if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
            {
                return false;
            }

            switch (hex.Length)
            {
                case 3: // #RGB → #FFRRGGBB
                    color = Color.FromArgb(0xFF, Nibble(value, 8), Nibble(value, 4), Nibble(value, 0));
                    return true;
                case 4: // #ARGB
                    color = Color.FromArgb(Nibble(value, 12), Nibble(value, 8), Nibble(value, 4), Nibble(value, 0));
                    return true;
                case 6: // #RRGGBB
                    color = Color.FromArgb(0xFF, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
                    return true;
                case 8: // #AARRGGBB
                    color = Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Expands one hex digit to a byte: F becomes FF, 8 becomes 88.</summary>
        private static byte Nibble(uint value, int shift) => (byte)(((value >> shift) & 0xF) * 0x11);
    }
}
