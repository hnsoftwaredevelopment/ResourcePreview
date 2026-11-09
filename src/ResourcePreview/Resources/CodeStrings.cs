using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ResourcePreview.Resources
{
    /// <summary>
    /// All string literals in the .cs and .vb files of a solution. A key used from code, such as
    /// FindResource("AccentBrush") or Resources["AccentBrush"], then does not count as unused.
    ///
    /// Each file is read once and remembered with its last write time, so a later scan only reads
    /// changed files. Uses no VS or WPF APIs.
    /// </summary>
    internal static class CodeStrings
    {
        private const long MaxFileSize = 1024 * 1024;

        private static readonly string[] Extensions = { ".cs", ".vb" };

        // "text" with escapes. Good enough for C# and VB: a key never contains quotes.
        private static readonly Regex StringLiteral = new Regex(@"""((?:[^""\\\r\n]|\\.)*)""", RegexOptions.Compiled);

        private static readonly ConcurrentDictionary<string, CachedFile> Cache =
            new ConcurrentDictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);

        private sealed class CachedFile
        {
            public CachedFile(DateTime lastWrite, HashSet<string> strings)
            {
                LastWrite = lastWrite;
                Strings = strings;
            }

            public DateTime LastWrite { get; }

            public HashSet<string> Strings { get; }
        }

        public static HashSet<string> Collect(string rootDirectory, CancellationToken cancellationToken)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in ResourceIndex.EnumerateFiles(rootDirectory, Extensions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.UnionWith(Read(file));
            }

            return result;
        }

        private static HashSet<string> Read(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (Cache.TryGetValue(file, out CachedFile cached) && cached.LastWrite == info.LastWriteTimeUtc)
                {
                    return cached.Strings;
                }

                HashSet<string> strings = info.Length > MaxFileSize
                    ? new HashSet<string>()
                    : Parse(File.ReadAllText(file));
                Cache[file] = new CachedFile(info.LastWriteTimeUtc, strings);
                return strings;
            }
            catch (IOException)
            {
                return new HashSet<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return new HashSet<string>();
            }
        }

        /// <summary>The contents of every string literal. Internal for testing.</summary>
        internal static HashSet<string> Parse(string code)
        {
            return new HashSet<string>(StringLiteral.Matches(code).Cast<Match>().Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        }
    }
}
