using System;
using System.Text.RegularExpressions;

namespace ResourcePreview.Resources
{
    /// <summary>
    /// Turns the text of a resource key into the key the index uses. Most keys are plain text: x:Key="AccentBrush".
    /// A key can also be a static member: x:Key="{x:Static res:TemplateKeys.ValueEditor}", used as
    /// {StaticResource {x:Static res:TemplateKeys.ValueEditor}}. We do not load the assembly, so we cannot read
    /// the value of the member. Two keys are the same when they name the same class and member; the prefix
    /// is left out because files may declare the namespace under another prefix.
    /// </summary>
    internal static class ResourceKeys
    {
        private const string StaticPrefix = "{x:Static ";

        /// <summary>
        /// Regex part for a key in a reference: a plain key, or one markup extension such as {x:Static a:B.C}.
        /// Uses the group name "key".
        /// </summary>
        internal const string KeyPattern = @"(?<key>\{[^{}""]*\}|[^\s,}{""]+)";

        /// <summary>Like <see cref="KeyPattern"/>, between double quotes (x:Key="...", ResourceKey="..."), where a key may contain spaces.</summary>
        internal const string QuotedKeyPattern = @"(?<key>\{[^{}""]*\}|[^""{]+)";

        // {x:Static res:TemplateKeys.ValueEditor}, {x:Static Member=res:TemplateKeys.ValueEditor}, {x:Static SystemColors.ControlBrushKey}
        private static readonly Regex StaticPattern = new Regex(
            @"^\{\s*(?:[\w-]+:)?Static\s+(?:Member\s*=\s*)?(?:[\w-]+:)?(?<member>[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)+)\s*\}$",
            RegexOptions.Compiled);

        /// <summary>
        /// The key as the index stores it: plain text stays as it is, a static member becomes
        /// "{x:Static TemplateKeys.ValueEditor}". Null for other markup, such as {x:Type Button}, which we do not support.
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            string trimmed = text.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return text; // WPF compares plain keys exactly, spaces included
            }

            Match match = StaticPattern.Match(trimmed);
            return match.Success ? StaticPrefix + match.Groups["member"].Value + "}" : null;
        }

        /// <summary>True for a key such as "{x:Static TemplateKeys.ValueEditor}".</summary>
        public static bool IsStatic(string key) => key != null && key.StartsWith(StaticPrefix, StringComparison.Ordinal);

        /// <summary>"TemplateKeys" for "{x:Static TemplateKeys.ValueEditor}", "Ns.Keys" for "{x:Static Ns.Keys.A}". Null for a plain key.</summary>
        public static string StaticTypeName(string key)
        {
            if (!IsStatic(key))
            {
                return null;
            }

            string member = key.Substring(StaticPrefix.Length, key.Length - StaticPrefix.Length - 1);
            int dot = member.LastIndexOf('.');
            return dot > 0 ? member.Substring(0, dot) : null;
        }

        /// <summary>
        /// The key in the dictionary the preview builds. XamlReader cannot resolve {x:Static res:...} because the
        /// preview never loads the project's own types, so a static key becomes plain text there.
        /// </summary>
        public static string PreviewKey(string key) =>
            IsStatic(key) ? "xStatic." + key.Substring(StaticPrefix.Length, key.Length - StaticPrefix.Length - 1) : key;
    }
}
