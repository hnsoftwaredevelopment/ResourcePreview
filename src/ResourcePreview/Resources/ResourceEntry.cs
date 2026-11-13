using System.Collections.Generic;
using System.Xml.Linq;

namespace ResourcePreview.Resources
{
    /// <summary>
    /// A resource definition found in a XAML file, such as &lt;GeometryGroup x:Key="Renew"&gt;.
    /// Stores a detached XML copy instead of a WPF object, because WPF objects are bound to one thread.
    /// Conversion to WPF happens later on the UI thread.
    /// </summary>
    internal sealed class ResourceEntry
    {
        public ResourceEntry(string key, XElement element, string filePath, int lineNumber, int column, IReadOnlyDictionary<string, string> namespaces, bool isRootScope = true, string keyText = null)
        {
            Key = key;
            KeyText = keyText ?? key;
            Element = element;
            FilePath = filePath;
            LineNumber = lineNumber;
            Column = column;
            Namespaces = namespaces;
            IsRootScope = isRootScope;
        }

        /// <summary>The key the index uses; see <see cref="ResourceKeys.Normalize"/>.</summary>
        public string Key { get; }

        /// <summary>
        /// The key as written in x:Key, such as "{x:Static res:TemplateKeys.ValueEditor}". Used for text we insert,
        /// so the prefix of the source file stays. The same as <see cref="Key"/> for a plain key.
        /// </summary>
        public string KeyText { get; }

        /// <summary>The reference to insert in XAML: {StaticResource AccentBrush}.</summary>
        public string StaticReference => "{StaticResource " + KeyText + "}";

        /// <summary>Copy of the element, detached from the source document.</summary>
        public XElement Element { get; }

        public string FilePath { get; }

        /// <summary>1-based line of the x:Key attribute, as Visual Studio shows it.</summary>
        public int LineNumber { get; }

        /// <summary>1-based column where the x:Key attribute starts. Used to place the caret.</summary>
        public int Column { get; }

        /// <summary>
        /// All xmlns declarations of the source file (prefix to namespace). The element may use prefixes
        /// declared at the file root, also inside attribute values such as {x:Static SystemColors.ControlBrush}.
        /// </summary>
        public IReadOnlyDictionary<string, string> Namespaces { get; }

        public string ElementTypeName => Element.Name.LocalName;

        /// <summary>
        /// True when the entry is in the file's main dictionary: the root ResourceDictionary, or
        /// Application.Resources / Window.Resources. False for nested scopes such as Grid.Resources
        /// or Style.Resources, which other files never see.
        /// </summary>
        public bool IsRootScope { get; }
    }
}
