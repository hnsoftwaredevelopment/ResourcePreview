using System.Text.RegularExpressions;
using ResourcePreview.Resources;

namespace ResourcePreview.QuickInfo
{
    /// <summary>
    /// A resource key found at the mouse position. Start and Length are offsets within the line.
    /// </summary>
    internal readonly struct ResourceReference
    {
        public ResourceReference(string key, int start, int length)
        {
            Key = key;
            Start = start;
            Length = length;
        }

        public string Key { get; }
        public int Start { get; }
        public int Length { get; }
    }

    /// <summary>
    /// Finds a resource reference at a position in a single line. Uses no VS APIs so it can be unit tested.
    ///
    /// Recognizes:
    ///   {StaticResource BuildInstaller}
    ///   {DynamicResource BuildInstaller}
    ///   {StaticResource ResourceKey=BuildInstaller}
    ///   &lt;StaticResource ResourceKey="BuildInstaller" /&gt;
    ///   x:Key="BuildInstaller"   (preview on the definition itself)
    ///   {StaticResource {x:Static res:TemplateKeys.ValueEditor}} and x:Key="{x:Static res:TemplateKeys.ValueEditor}"
    /// </summary>
    internal static class ResourceReferenceFinder
    {
        private static readonly Regex[] Patterns =
        {
            new Regex(@"\{\s*(?:StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?" + ResourceKeys.KeyPattern + @"[^}]*\}", RegexOptions.Compiled),
            new Regex(@"<\s*(?:StaticResource|DynamicResource)\s+ResourceKey\s*=\s*""" + ResourceKeys.QuotedKeyPattern + @"""", RegexOptions.Compiled),
            new Regex(@"\bx:Key\s*=\s*""" + ResourceKeys.QuotedKeyPattern + @"""", RegexOptions.Compiled),
        };

        public static bool TryFind(string lineText, int positionInLine, out ResourceReference reference) =>
            TryFind(lineText, positionInLine, Patterns.Length, out reference);

        /// <summary>
        /// Like <see cref="TryFind(string, int, out ResourceReference)"/>, but only references, not x:Key definitions.
        /// Used by Go To Resource Definition: on a definition there is nothing to go to.
        /// </summary>
        public static bool TryFindReference(string lineText, int positionInLine, out ResourceReference reference) =>
            TryFind(lineText, positionInLine, Patterns.Length - 1, out reference);

        private static bool TryFind(string lineText, int positionInLine, int patternCount, out ResourceReference reference)
        {
            for (int i = 0; i < patternCount; i++)
            {
                Regex pattern = Patterns[i];
                foreach (Match match in pattern.Matches(lineText))
                {
                    // Inclusive end so hovering the last character still counts.
                    if (positionInLine >= match.Index && positionInLine <= match.Index + match.Length)
                    {
                        string key = ResourceKeys.Normalize(match.Groups["key"].Value);
                        if (key == null)
                        {
                            continue; // {x:Type Button} and other markup we do not support
                        }

                        reference = new ResourceReference(key, match.Index, match.Length);
                        return true;
                    }
                }
            }

            reference = default;
            return false;
        }
    }
}
