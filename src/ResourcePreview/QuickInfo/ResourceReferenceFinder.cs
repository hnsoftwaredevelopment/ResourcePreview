using System.Text.RegularExpressions;

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
    /// </summary>
    internal static class ResourceReferenceFinder
    {
        private static readonly Regex[] Patterns =
        {
            new Regex(@"\{\s*(?:StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?(?<key>[^\s,}{]+)[^}]*\}", RegexOptions.Compiled),
            new Regex(@"<\s*(?:StaticResource|DynamicResource)\s+ResourceKey\s*=\s*""(?<key>[^""{]+)""", RegexOptions.Compiled),
            new Regex(@"\bx:Key\s*=\s*""(?<key>[^""{]+)""", RegexOptions.Compiled),
        };

        public static bool TryFind(string lineText, int positionInLine, out ResourceReference reference)
        {
            foreach (Regex pattern in Patterns)
            {
                foreach (Match match in pattern.Matches(lineText))
                {
                    // Inclusive end so hovering the last character still counts.
                    if (positionInLine >= match.Index && positionInLine <= match.Index + match.Length)
                    {
                        reference = new ResourceReference(match.Groups["key"].Value, match.Index, match.Length);
                        return true;
                    }
                }
            }

            reference = default;
            return false;
        }
    }
}
