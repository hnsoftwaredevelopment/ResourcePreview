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

        // The list of code files per root, so a scan does not walk the whole folder tree each time.
        // A watcher clears it when a file or folder is added, removed or renamed.
        private static readonly ConcurrentDictionary<string, FileList> Lists =
            new ConcurrentDictionary<string, FileList>(StringComparer.OrdinalIgnoreCase);

        private sealed class FileList
        {
            public volatile string[] Files;

            /// <summary>Raised on every change, so a list read during a change is not kept.</summary>
            public int Version;

            public FileSystemWatcher Watcher;
        }

        public static HashSet<string> Collect(string rootDirectory, CancellationToken cancellationToken)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in GetFiles(rootDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.UnionWith(Read(file));
            }

            return result;
        }

        /// <summary>The .cs and .vb files under the root, from the cached list when nothing was added or removed.</summary>
        internal static string[] GetFiles(string rootDirectory)
        {
            FileList list = Lists.GetOrAdd(rootDirectory, Watch);
            string[] files = list.Files;
            if (files == null)
            {
                int version = Volatile.Read(ref list.Version);
                files = ResourceIndex.EnumerateFiles(rootDirectory, Extensions).ToArray();
                if (Volatile.Read(ref list.Version) == version)
                {
                    list.Files = files;
                }
            }

            return files;
        }

        private static FileList Watch(string rootDirectory)
        {
            var list = new FileList();
            try
            {
                var watcher = new FileSystemWatcher(rootDirectory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                FileSystemEventHandler changed = (s, e) => Invalidate(list, e.FullPath);
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += (s, e) => Invalidate(list, e.FullPath);
                watcher.Error += (s, e) =>
                {
                    Interlocked.Increment(ref list.Version);
                    list.Files = null;
                };
                watcher.EnableRaisingEvents = true;
                list.Watcher = watcher;
            }
            catch (Exception)
            {
                // No watcher (network drive, permissions): the list is read once and not refreshed.
            }

            return list;
        }

        /// <summary>A code file, or a folder (no extension), was added, removed or renamed.</summary>
        private static void Invalidate(FileList list, string path)
        {
            string extension = Path.GetExtension(path);
            if (extension.Length == 0 || Extensions.Any(e => extension.Equals(e, StringComparison.OrdinalIgnoreCase)))
            {
                Interlocked.Increment(ref list.Version);
                list.Files = null;
            }
        }

        /// <summary>Stops the watchers and forgets the cached files. Called when the solution closes.</summary>
        public static void ReleaseAll()
        {
            foreach (string root in Lists.Keys.ToList())
            {
                if (Lists.TryRemove(root, out FileList list))
                {
                    list.Watcher?.Dispose();
                }
            }

            Cache.Clear();
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
