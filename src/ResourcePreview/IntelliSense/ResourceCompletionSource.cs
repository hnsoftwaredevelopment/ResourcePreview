using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using ResourcePreview.Options;
using ResourcePreview.Preview;
using ResourcePreview.Resources;
using Task = System.Threading.Tasks.Task;

namespace ResourcePreview.IntelliSense
{
    /// <summary>
    /// Adds icons to the IntelliSense list of the XAML editor after {StaticResource and {DynamicResource.
    ///
    /// The XAML editor fills that list with its own completion source ("XAML Completion Source", the
    /// classic ICompletionSource API). We add no items of our own, which would give every key twice.
    /// Instead this source runs after the XAML one and decorates its items: an icon, and a note in the
    /// item's property bag that <see cref="ResourceCompletionToolTipProvider"/> uses for the tooltip.
    /// </summary>
    [Export(typeof(ICompletionSourceProvider))]
    [Name("XAML Resource Preview Completion")]
    [ContentType("XAML")]
    [Order(After = "XAML Completion Source")]
    internal sealed class ResourceCompletionSourceProvider : ICompletionSourceProvider
    {
        [Import]
        internal ITextDocumentFactoryService TextDocumentFactoryService { get; set; }

        public ICompletionSource TryCreateCompletionSource(ITextBuffer textBuffer)
        {
            if (!TextDocumentFactoryService.TryGetTextDocument(textBuffer, out ITextDocument document)
                || !document.FilePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return textBuffer.Properties.GetOrCreateSingletonProperty(typeof(ResourceCompletionSource), () => new ResourceCompletionSource(textBuffer, document));
        }
    }

    internal sealed class ResourceCompletionSource : ICompletionSource
    {
        // Larger lists are left alone, so typing stays fast in a solution with thousands of keys.
        private const int MaxDecoratedItems = 500;

        /// <summary>The caret is where a resource key goes: "{StaticResource Acc", "{DynamicResource ResourceKey=".</summary>
        private static readonly Regex KeyPosition =
            new Regex(@"\{\s*(?:StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?[^\s,{}=]*$", RegexOptions.Compiled);

        private readonly ITextBuffer _textBuffer;
        private readonly ITextDocument _document;

        public ResourceCompletionSource(ITextBuffer textBuffer, ITextDocument document)
        {
            _textBuffer = textBuffer;
            _document = document;
        }

        internal static bool IsKeyPosition(string textBeforeCaret) => KeyPosition.IsMatch(textBeforeCaret);

        public void AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> completionSets)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!SettingsService.Current.ShowInCompletion)
            {
                return;
            }

            SnapshotPoint? caret = session.GetTriggerPoint(_textBuffer.CurrentSnapshot);
            if (!caret.HasValue)
            {
                return;
            }

            ITextSnapshotLine line = caret.Value.GetContainingLine();
            if (!IsKeyPosition(caret.Value.Snapshot.GetText(line.Start, caret.Value.Position - line.Start.Position)))
            {
                return;
            }

            string filePath = _document.FilePath;
            ResourceIndex index = ResourceIndex.ForFile(filePath);
            if (!index.IsReady)
            {
                // The first scan reads every .xaml file. Start it in the background; the next list has icons.
                Task.Run(() => index.Find(string.Empty, filePath, CancellationToken.None)).Forget();
                return;
            }

            // No SetLiveText here: parsing the whole file on the UI thread for every list made typing slow in
            // large files. LiveTextTracker keeps the index up to date with the open editor in the background.

            // VS calls this again after each Recalculate, so items the XAML editor adds later are decorated then.
            Decorate(index, filePath, completionSets);
        }

        private static void Decorate(ResourceIndex index, string filePath, IEnumerable<CompletionSet> completionSets)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Brush foreground = GetForeground();
            foreach (CompletionSet set in completionSets.ToList())
            {
                if (set.Completions.Count > MaxDecoratedItems)
                {
                    continue;
                }

                foreach (Completion item in set.Completions)
                {
                    if (item.Properties.ContainsProperty(typeof(ResourceEntry)))
                    {
                        continue; // already done
                    }

                    // The definition WPF would use here, as in the tooltip on hover.
                    ResourceEntry entry = index.Find(item.DisplayText, filePath, CancellationToken.None).FirstOrDefault();
                    if (entry == null)
                    {
                        continue;
                    }

                    item.Properties.AddProperty(typeof(ResourceEntry), entry);
                    item.Properties.AddProperty(typeof(ResourceIndex), index);

                    ImageSource icon = CompletionIconFactory.Get(index, entry, foreground);
                    if (icon != null)
                    {
                        item.IconSource = icon;
                    }
                }
            }
        }

        /// <summary>Text color of the IntelliSense list in the current theme, for geometries without a color.</summary>
        private static Brush GetForeground()
        {
            var brush = new SolidColorBrush(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey).ToMediaColor());
            brush.Freeze();
            return brush;
        }

        public void Dispose()
        {
        }
    }

    internal static class ColorExtensions
    {
        public static Color ToMediaColor(this System.Drawing.Color color) => Color.FromArgb(color.A, color.R, color.G, color.B);
    }
}
