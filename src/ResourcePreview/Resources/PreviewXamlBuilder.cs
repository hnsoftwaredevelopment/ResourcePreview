using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace ResourcePreview.Resources
{
    /// <summary>Result of <see cref="PreviewXamlBuilder.Build"/>.</summary>
    internal sealed class PreviewXaml
    {
        public PreviewXaml(string xaml, IReadOnlyList<string> missingKeys, string unloadableType = null)
        {
            Xaml = xaml;
            MissingKeys = missingKeys;
            UnloadableType = unloadableType;
        }

        /// <summary>A complete &lt;ResourceDictionary&gt; as text, ready for XamlReader.Parse.</summary>
        public string Xaml { get; }

        /// <summary>Referenced keys that were not found anywhere in the solution.</summary>
        public IReadOnlyList<string> MissingKeys { get; }

        /// <summary>
        /// A type Visual Studio cannot load, such as "local:Book". When set, XamlReader.Parse would fail,
        /// so the renderer skips loading. Null when every type is loadable.
        /// </summary>
        public string UnloadableType { get; }
    }

    /// <summary>
    /// Builds a standalone ResourceDictionary with the requested resource and all its dependencies,
    /// deepest dependency first. StaticResource resolves during parsing and only sees entries above it.
    /// Uses text and XML only, no WPF, so it runs on a background thread.
    /// </summary>
    internal static class PreviewXamlBuilder
    {
        private static readonly XNamespace PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace DesignerNamespace = "http://schemas.microsoft.com/expression/blend/2008";
        private static readonly XNamespace CompatibilityNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";

        private static readonly Regex ReferencePattern =
            new Regex(@"\{\s*(?:StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?(?<key>[^\s,}{]+)", RegexOptions.Compiled);

        private static readonly Regex DynamicResourcePattern =
            new Regex(@"\{\s*DynamicResource\b", RegexOptions.Compiled);

        private static readonly Regex PackUriPattern =
            new Regex(@"pack://application:,,,/(?:(?<assembly>[^;/]+);component/)?(?<path>[^""'\s}]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] ImageSourceAttributes = { "Source", "UriSource", "ImageSource" };

        /// <summary>Component path without the pack prefix, such as "/MyApp;component/Fonts/".</summary>
        private static readonly Regex ComponentPathPattern =
            new Regex(@"^/(?<assembly>[^;/]+);component/(?<path>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// .NET (Core) projects map core types to System.Runtime or System.Private.CoreLib.
        /// Visual Studio runs on .NET Framework, where they live in mscorlib.
        /// </summary>
        private static readonly Regex CoreLibraryNamespacePattern =
            new Regex(@"^(?<ns>clr-namespace:[^;]+);assembly=(?:System\.Runtime|System\.Private\.CoreLib|netstandard)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static PreviewXaml Build(ResourceIndex index, ResourceEntry target, CancellationToken cancellationToken)
        {
            var ordered = new List<ResourceEntry>();
            var missing = new List<string>();
            var done = new HashSet<string>(StringComparer.Ordinal);
            var inProgress = new HashSet<string>(StringComparer.Ordinal);

            Visit(target);

            // Depth-first: an entry is added only after all its dependencies.
            void Visit(ResourceEntry entry)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (done.Contains(entry.Key) || !inProgress.Add(entry.Key))
                {
                    return; // already done, or a cycle (A to B to A)
                }

                foreach (string dependencyKey in FindReferencedKeys(entry.Element))
                {
                    if (dependencyKey == entry.Key || done.Contains(dependencyKey))
                    {
                        continue;
                    }

                    ResourceEntry dependency = index.Find(dependencyKey, entry.FilePath, cancellationToken).FirstOrDefault();
                    if (dependency == null)
                    {
                        if (!missing.Contains(dependencyKey))
                        {
                            missing.Add(dependencyKey);
                        }
                    }
                    else
                    {
                        Visit(dependency);
                    }
                }

                inProgress.Remove(entry.Key);
                done.Add(entry.Key);
                ordered.Add(entry);
            }

            string unloadable = ordered.Select(FindUnloadableType).FirstOrDefault(t => t != null);
            return new PreviewXaml(CreateDictionary(index, ordered).ToString(SaveOptions.DisableFormatting), missing, unloadable);
        }

        /// <summary>Namespaces Visual Studio can always load. d: and mc: are listed because they get stripped.</summary>
        private static readonly HashSet<string> LoadableUris = new HashSet<string>(StringComparer.Ordinal)
        {
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation/options",
            "http://schemas.microsoft.com/winfx/2006/xaml/composite-font",
            "http://schemas.microsoft.com/netfx/2007/xaml/presentation",
            "http://schemas.microsoft.com/netfx/2009/xaml/presentation",
            "http://schemas.microsoft.com/winfx/2006/xaml",
            "http://www.w3.org/XML/1998/namespace",
            "http://schemas.microsoft.com/expression/blend/2008",
            "http://schemas.openxmlformats.org/markup-compatibility/2006",
        };

        /// <summary>.NET Framework and WPF assemblies that are always loaded in the Visual Studio process.</summary>
        private static readonly HashSet<string> LoadableAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib", "System", "System.Core", "System.Xml", "System.Xaml", "System.Drawing",
            "WindowsBase", "PresentationCore", "PresentationFramework",
        };

        /// <summary>Matches prefixed names in values: {x:Type local:Book}, {x:Static local:Icons.Save}, Property="local:Attached.Value".</summary>
        private static readonly Regex PrefixedNamePattern =
            new Regex(@"(?<![\w.:/])(?<prefix>[A-Za-z_][\w-]*):(?<name>[A-Za-z_][\w.]*)", RegexOptions.Compiled);

        /// <summary>
        /// Returns the first type in the resource that Visual Studio cannot load, or null.
        /// Checking up front avoids a XamlReader exception, which is slow and breaks the debugger
        /// when "break when thrown" is on.
        /// </summary>
        internal static string FindUnloadableType(ResourceEntry entry) => FindUnloadableType(entry.Element, entry.Namespaces);

        /// <param name="outerNamespaces">
        /// Prefixes of the source file. The stored element is a detached copy, so declarations on the
        /// original root are only available here.
        /// </param>
        internal static string FindUnloadableType(XElement element, IReadOnlyDictionary<string, string> outerNamespaces = null)
        {
            string NamespaceOf(XElement node, string prefix)
            {
                XNamespace declared = node.GetNamespaceOfPrefix(prefix);
                if (declared != null)
                {
                    return declared.NamespaceName;
                }

                return outerNamespaces != null && outerNamespaces.TryGetValue(prefix, out string outer) ? outer : null;
            }

            string DisplayName(XElement node, XName name)
            {
                string prefix = node.GetPrefixOfNamespace(name.Namespace)
                    ?? outerNamespaces?.FirstOrDefault(p => p.Key.Length > 0 && p.Value == name.NamespaceName).Key;
                return string.IsNullOrEmpty(prefix) ? name.LocalName : prefix + ":" + name.LocalName;
            }

            foreach (XElement node in element.DescendantsAndSelf())
            {
                if (!IsLoadableNamespace(node.Name.NamespaceName))
                {
                    return DisplayName(node, node.Name);
                }

                foreach (XAttribute attribute in node.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration)
                    {
                        continue;
                    }

                    if (attribute.Name.Namespace != XNamespace.None && !IsLoadableNamespace(attribute.Name.NamespaceName))
                    {
                        return DisplayName(node, attribute.Name);
                    }

                    foreach (Match match in PrefixedNamePattern.Matches(attribute.Value))
                    {
                        string ns = NamespaceOf(node, match.Groups["prefix"].Value);
                        if (ns != null && !IsLoadableNamespace(ns))
                        {
                            return match.Value;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Returns whether Visual Studio can load types from this namespace:
        ///   clr-namespace:SampleApp                          no, the user's own project
        ///   clr-namespace:MyLib;assembly=MyLib               only if MyLib is already loaded
        ///   clr-namespace:System;assembly=System.Runtime     yes, maps to mscorlib
        ///   http://metro.mahapps.com/winfx/xaml/controls     no, a NuGet package
        /// </summary>
        internal static bool IsLoadableNamespace(string ns)
        {
            if (string.IsNullOrEmpty(ns) || LoadableUris.Contains(ns))
            {
                return true;
            }

            if (!ns.StartsWith("clr-namespace:", StringComparison.Ordinal))
            {
                return false;
            }

            string normalized = NormalizeClrNamespace(ns);
            int assemblyIndex = normalized.IndexOf(";assembly=", StringComparison.Ordinal);
            if (assemblyIndex < 0)
            {
                return false; // without assembly= XAML means the project's own assembly
            }

            string assembly = normalized.Substring(assemblyIndex + ";assembly=".Length).Trim();
            return LoadableAssemblies.Contains(assembly)
                || AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, assembly, StringComparison.OrdinalIgnoreCase));
        }


        /// <summary>All resource keys referenced by the element or its descendants.</summary>
        internal static IEnumerable<string> FindReferencedKeys(XElement element)
        {
            var keys = new List<string>();

            foreach (XElement node in element.DescendantsAndSelf())
            {
                foreach (XAttribute attribute in node.Attributes())
                {
                    foreach (Match match in ReferencePattern.Matches(attribute.Value))
                    {
                        keys.Add(match.Groups["key"].Value);
                    }
                }

                // Element syntax: <StaticResource ResourceKey="Foo" />
                if (node.Name.LocalName == "StaticResource" || node.Name.LocalName == "DynamicResource")
                {
                    string key = (string)node.Attribute("ResourceKey");
                    if (!string.IsNullOrEmpty(key) && !key.StartsWith("{", StringComparison.Ordinal))
                    {
                        keys.Add(key);
                    }
                }
            }

            return keys.Distinct(StringComparer.Ordinal);
        }

        private static XElement CreateDictionary(ResourceIndex index, IEnumerable<ResourceEntry> entries)
        {
            var root = new XElement(
                PresentationNamespace + "ResourceDictionary",
                new XAttribute("xmlns", PresentationNamespace.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "x", ResourceIndex.XamlNamespace.NamespaceName));

            var declaredPrefixes = new HashSet<string> { string.Empty, "x" };

            foreach (ResourceEntry entry in entries)
            {
                XElement copy = PrepareElement(index, entry);

                // LINQ to XML does not see prefixes inside attribute values, such as {x:Static sys:Math.PI}.
                // Copy the source file's declaration for every prefix that appears in the text.
                string text = copy.ToString();
                foreach (KeyValuePair<string, string> ns in entry.Namespaces)
                {
                    if (ns.Key.Length == 0 || declaredPrefixes.Contains(ns.Key))
                    {
                        continue;
                    }

                    if (text.IndexOf(ns.Key + ":", StringComparison.Ordinal) >= 0)
                    {
                        root.Add(new XAttribute(XNamespace.Xmlns + ns.Key, NormalizeClrNamespace(ns.Value)));
                        declaredPrefixes.Add(ns.Key);
                    }
                }

                root.Add(copy);
            }

            return root;
        }

        private static XElement PrepareElement(ResourceIndex index, ResourceEntry entry)
        {
            var copy = new XElement(entry.Element);

            // XamlReader rejects d: and mc: attributes without mc:Ignorable, so strip them.
            foreach (XElement node in copy.DescendantsAndSelf())
            {
                node.Attributes()
                    .Where(a => a.Name.Namespace == DesignerNamespace || a.Name.Namespace == CompatibilityNamespace)
                    .ToList()
                    .ForEach(a => a.Remove());
            }

            string projectDirectory = ResourceIndex.FindProjectDirectory(entry.FilePath);
            string fileDirectory = Path.GetDirectoryName(entry.FilePath);

            foreach (XElement node in copy.DescendantsAndSelf().ToList())
            {
                // For example <sys:Double> from a .NET 8 project: System.Runtime becomes mscorlib.
                string elementNamespace = node.Name.NamespaceName;
                string normalized = NormalizeClrNamespace(elementNamespace);
                if (normalized != elementNamespace)
                {
                    node.Name = XNamespace.Get(normalized) + node.Name.LocalName;
                }

                foreach (XAttribute declaration in node.Attributes().Where(a => a.IsNamespaceDeclaration).ToList())
                {
                    declaration.Value = NormalizeClrNamespace(declaration.Value);
                }

                // <FontFamily x:Key="Brand">./Fonts/#Inter</FontFamily>: resolve the folder to a path on disk.
                if (node.Name.LocalName == "FontFamily" && !node.HasElements && !node.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.LocalName != "Key"))
                {
                    node.Value = ResolveFontFamily(index, node.Value, projectDirectory, fileDirectory);
                }

                // DynamicResource never resolves in a standalone dictionary because there is no visual tree.
                // StaticResource is safe here since dependencies are already placed above.
                if (node.Name.LocalName == "DynamicResource")
                {
                    node.Name = node.Name.Namespace + "StaticResource";
                }

                foreach (XAttribute attribute in node.Attributes().Where(a => !a.IsNamespaceDeclaration))
                {
                    string value = DynamicResourcePattern.Replace(attribute.Value, "{StaticResource");

                    // Font references contain a #, which must not become part of the file path.
                    if (attribute.Name.LocalName == "FontFamily" || (attribute.Name.LocalName == "Source" && node.Name.LocalName == "FontFamily"))
                    {
                        attribute.Value = ResolveFontFamily(index, value, projectDirectory, fileDirectory);
                        continue;
                    }

                    value = ResolvePackUris(index, value, projectDirectory);

                    if (ImageSourceAttributes.Contains(attribute.Name.LocalName))
                    {
                        value = ResolveRelativePath(value, projectDirectory, fileDirectory);
                    }

                    attribute.Value = value;
                }
            }

            return copy;
        }

        /// <summary>
        /// Pack URIs only work inside the running app, so map them to disk:
        /// "pack://application:,,,/Images/save.png" becomes "file:///C:/.../MyApp/Images/save.png".
        /// </summary>
        private static string ResolvePackUris(ResourceIndex index, string value, string projectDirectory)
        {
            return PackUriPattern.Replace(value, match =>
            {
                string directory = projectDirectory;
                if (match.Groups["assembly"].Success)
                {
                    directory = index.FindProjectDirectoryByAssemblyName(match.Groups["assembly"].Value) ?? projectDirectory;
                }

                return ToFileUri(directory, match.Groups["path"].Value) ?? match.Value;
            });
        }

        /// <summary>
        /// "/Images/save.png" is relative to the project folder, "Images/save.png" to the XAML file's folder.
        /// Markup extensions and absolute URIs are returned unchanged.
        /// </summary>
        private static string ResolveRelativePath(string value, string projectDirectory, string fileDirectory)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("{", StringComparison.Ordinal) || value.Contains("://"))
            {
                return value;
            }

            return value.StartsWith("/", StringComparison.Ordinal)
                ? ToFileUri(projectDirectory, value.TrimStart('/')) ?? value
                : ToFileUri(fileDirectory, value) ?? value;
        }

        /// <summary>
        /// "clr-namespace:System;assembly=System.Runtime" becomes "clr-namespace:System;assembly=mscorlib".
        /// Other namespaces are returned unchanged.
        /// </summary>
        internal static string NormalizeClrNamespace(string ns)
        {
            return CoreLibraryNamespacePattern.Replace(ns, "${ns};assembly=mscorlib");
        }

        /// <summary>
        /// Resolves font locations before #Name to file URIs, such as
        /// "./Fonts/#Inter" (relative to the XAML file) becomes "file:///C:/.../Fonts/#Inter".
        /// Also handles "/Fonts/#Inter", "/MyApp;component/Fonts/#Inter" and pack URIs. Each name in a
        /// list is resolved separately. Names without # and markup extensions are returned unchanged.
        /// </summary>
        internal static string ResolveFontFamily(ResourceIndex index, string value, string projectDirectory, string fileDirectory)
        {
            if (string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith("{", StringComparison.Ordinal) || value.IndexOf('#') < 0)
            {
                return value;
            }

            // Split on commas, but not the ones in "pack://application:,,,".
            const string PackPrefix = "application:,,,";
            const string Placeholder = "application:\u0001";

            IEnumerable<string> parts = value.Replace(PackPrefix, Placeholder).Split(',').Select(part =>
            {
                string trimmed = part.Replace(Placeholder, PackPrefix).Trim();
                int hash = trimmed.LastIndexOf('#');
                if (hash < 0)
                {
                    return trimmed;
                }

                string location = trimmed.Substring(0, hash);
                string resolved = ResolveFontLocation(index, location, projectDirectory, fileDirectory);
                return (resolved ?? location) + trimmed.Substring(hash);
            });

            return string.Join(", ", parts);
        }

        private static string ResolveFontLocation(ResourceIndex index, string location, string projectDirectory, string fileDirectory)
        {
            string directory;
            string path;

            Match pack = PackUriPattern.Match(location + "x"); // the "x" allows an empty path after ,,,/
            Match component = ComponentPathPattern.Match(location);

            if (location.Length == 0)
            {
                // "#Inter": a font in the same folder as the XAML file
                directory = fileDirectory;
                path = string.Empty;
            }
            else if (pack.Success && pack.Index == 0)
            {
                directory = pack.Groups["assembly"].Success
                    ? index.FindProjectDirectoryByAssemblyName(pack.Groups["assembly"].Value) ?? projectDirectory
                    : projectDirectory;
                path = pack.Groups["path"].Value;
                path = path.Substring(0, path.Length - 1);
            }
            else if (component.Success)
            {
                directory = index.FindProjectDirectoryByAssemblyName(component.Groups["assembly"].Value) ?? projectDirectory;
                path = component.Groups["path"].Value;
            }
            else if (location.Contains("://") || Path.IsPathRooted(location) && !location.StartsWith("/", StringComparison.Ordinal))
            {
                return location; // already absolute: file:///... or C:\Fonts
            }
            else if (location.StartsWith("/", StringComparison.Ordinal))
            {
                directory = projectDirectory;
                path = location.TrimStart('/');
            }
            else
            {
                directory = fileDirectory;
                path = location;
            }

            string uri = ToFileUri(directory, path);
            if (uri == null)
            {
                return null;
            }

            // WPF treats a location as a folder only when it ends with a slash.
            bool isDirectory = path.Length == 0 || path.EndsWith("/", StringComparison.Ordinal) || path.EndsWith("\\", StringComparison.Ordinal);
            return isDirectory && !uri.EndsWith("/", StringComparison.Ordinal) ? uri + "/" : uri;
        }

        private static string ToFileUri(string directory, string relativePath)
        {
            try
            {
                string fullPath = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(relativePath).Replace('/', Path.DirectorySeparatorChar)));
                return new Uri(fullPath).AbsoluteUri;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
