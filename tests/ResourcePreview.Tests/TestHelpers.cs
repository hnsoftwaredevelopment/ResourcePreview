using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace ResourcePreview.Tests
{
    /// <summary>Paths to files in the repository, found from the folder of the test DLL.</summary>
    internal static class RepoPaths
    {
        private static readonly Lazy<string> RootLazy = new Lazy<string>(() =>
        {
            // Use the test DLL folder, not AppDomain.BaseDirectory: under vstest.console that points
            // to the test platform folder in the VS installation.
            string start = Path.GetDirectoryName(typeof(RepoPaths).Assembly.Location);
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ResourcePreview.sln")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException("ResourcePreview.sln not found above " + start);
        });

        public static string Root => RootLazy.Value;

        public static string Source => Path.Combine(Root, "src", "ResourcePreview");

        public static string SampleApp => Path.Combine(Root, "samples", "SampleApp");

        public static string InSource(params string[] parts) => Path.Combine(Source, Path.Combine(parts));
    }

    /// <summary>A temp folder per test, so each test gets its own ResourceIndex (it caches per root folder).</summary>
    internal sealed class TempSolution : IDisposable
    {
        public TempSolution(string solutionName = "Test.sln")
        {
            Root = Path.Combine(Path.GetTempPath(), "ResourcePreviewTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            if (solutionName != null)
            {
                File.WriteAllText(Path.Combine(Root, solutionName), string.Empty);
            }
        }

        public string Root { get; }

        public string Write(string relativePath, string content)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // FileSystemWatcher may still hold the folder; Windows cleans up the temp folder later.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// WPF objects must be created on an STA thread. MSTest runs tests on an MTA thread by default,
    /// so WPF code runs on a separate STA thread and the test waits for it.
    /// </summary>
    internal static class Sta
    {
        public static T Run<T>(Func<T> action)
        {
            T result = default;
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = action();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (error != null)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            return result;
        }

        public static void Run(Action action) => Run(() => { action(); return 0; });
    }

    /// <summary>Small XAML snippets shared by several tests.</summary>
    internal static class Xaml
    {
        public const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        public const string X = "http://schemas.microsoft.com/winfx/2006/xaml";

        public static string Dictionary(string body, string extraNamespaces = "")
        {
            return $"<ResourceDictionary xmlns=\"{Presentation}\" xmlns:x=\"{X}\" {extraNamespaces}>\n{body}\n</ResourceDictionary>";
        }

        public static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n');
    }
}
