using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ResourcePreview.Resources
{
    /// <summary>A piece of text to replace: start and length in the file text.</summary>
    internal readonly struct KeySpan
    {
        public KeySpan(int start, int length)
        {
            Start = start;
            Length = length;
        }

        public int Start { get; }

        public int Length { get; }
    }

    /// <summary>One file a rename changes, with how many definitions and references it has.</summary>
    internal sealed class RenameFile
    {
        public RenameFile(string filePath, int definitions, int references)
        {
            FilePath = filePath;
            Definitions = definitions;
            References = references;
        }

        public string FilePath { get; }

        public int Definitions { get; }

        public int References { get; }
    }

    /// <summary>
    /// The text side of "Rename Resource Key": which files change, where in the text, and whether the
    /// new name is allowed. Uses no VS or WPF APIs, so all of it is unit tested. Applying the changes to
    /// the editor buffers happens in <c>RenameResourceKeyCommand</c>.
    /// </summary>
    internal static class KeyRenamer
    {
        private static readonly Regex CommentPattern = new Regex("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

        // Characters that would break the XAML or the markup extension: {StaticResource A B} or x:Key="A"B".
        private static readonly Regex ValidKey = new Regex(@"^[^\s""'{}<>,=&]+$", RegexOptions.Compiled);

        /// <summary>The files that define or use the key, from the index. Sorted by path.</summary>
        public static IReadOnlyList<RenameFile> Plan(ResourceIndex index, string key, CancellationToken cancellationToken)
        {
            MergeGraph graph = index.GetMergeGraph(cancellationToken);
            return graph.Files
                .Select(f => new RenameFile(
                    f.Key,
                    f.Value.Entries.Count(e => string.Equals(e.Key, key, StringComparison.Ordinal)),
                    f.Value.Uses.Count(u => string.Equals(u.Key, key, StringComparison.Ordinal))))
                .Where(f => f.Definitions + f.References > 0)
                .OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Null when the name can be used, otherwise why not: "invalid" or "exists".</summary>
        public static RenameRejection? Check(ResourceIndex index, string oldKey, string newKey, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(newKey) || !ValidKey.IsMatch(newKey))
            {
                return RenameRejection.Invalid;
            }

            if (string.Equals(oldKey, newKey, StringComparison.Ordinal))
            {
                return RenameRejection.Unchanged;
            }

            // Two keys with the same name would make one of them win silently, or not compile.
            MergeGraph graph = index.GetMergeGraph(cancellationToken);
            bool exists = graph.Files.Values.Any(f => f.Entries.Any(e => string.Equals(e.Key, newKey, StringComparison.Ordinal)));
            return exists ? RenameRejection.Exists : (RenameRejection?)null;
        }

        /// <summary>
        /// The spans of the key in a file: x:Key="Key", {StaticResource Key}, {DynamicResource ResourceKey=Key}
        /// and &lt;StaticResource ResourceKey="Key"/&gt;. Not inside comments. Sorted by position.
        /// </summary>
        public static IReadOnlyList<KeySpan> FindSpans(string text, string key)
        {
            string escaped = Regex.Escape(key);
            var patterns = new[]
            {
                new Regex(@"\bx:Key\s*=\s*([""'])(?<key>" + escaped + @")\1"),
                new Regex(@"\{\s*(?:StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?(?<key>" + escaped + @")(?=[\s,}])"),
                new Regex(@"<\s*(?:StaticResource|DynamicResource)\s+ResourceKey\s*=\s*([""'])(?<key>" + escaped + @")\1"),
            };

            List<KeySpan> comments = CommentPattern.Matches(text).Cast<Match>().Select(m => new KeySpan(m.Index, m.Length)).ToList();

            return patterns
                .SelectMany(p => p.Matches(text).Cast<Match>())
                .Select(m => new KeySpan(m.Groups["key"].Index, m.Groups["key"].Length))
                .Where(span => !comments.Any(c => span.Start >= c.Start && span.Start < c.Start + c.Length))
                .GroupBy(span => span.Start)
                .Select(g => g.First())
                .OrderBy(span => span.Start)
                .ToList();
        }

        /// <summary>The text after the rename. Used by the tests; the editor applies the spans itself.</summary>
        public static string Apply(string text, string key, string newKey)
        {
            foreach (KeySpan span in FindSpans(text, key).Reverse())
            {
                text = text.Substring(0, span.Start) + newKey + text.Substring(span.Start + span.Length);
            }

            return text;
        }
    }

    internal enum RenameRejection
    {
        Invalid,
        Unchanged,
        Exists,
    }
}
