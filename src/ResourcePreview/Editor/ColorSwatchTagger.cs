using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using ResourcePreview.Options;
using ResourcePreview.Resources;

namespace ResourcePreview.Editor
{
    /// <summary>
    /// Creates a <see cref="ColorSwatchTagger"/> per .xaml editor. An IViewTaggerProvider, not an
    /// ITaggerProvider, because an IntraTextAdornmentTag belongs to one view.
    /// </summary>
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("XAML")]
    [ContentType("XML")]
    [TagType(typeof(IntraTextAdornmentTag))]
    internal sealed class ColorSwatchTaggerProvider : IViewTaggerProvider
    {
        [Import]
        internal ITextDocumentFactoryService TextDocumentFactoryService { get; set; }

        [Import]
        internal JoinableTaskContext JoinableTaskContext { get; set; }

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer)
            where T : ITag
        {
            // Only the top buffer of the view: projection buffers would show the swatches twice.
            if (textView == null || buffer != textView.TextBuffer
                || !TextDocumentFactoryService.TryGetTextDocument(buffer, out ITextDocument document)
                || !document.FilePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // One tagger per view; VS may ask more than once.
            return textView.Properties.GetOrCreateSingletonProperty(
                () => new ColorSwatchTagger(textView, buffer, document, JoinableTaskContext.Factory)) as ITagger<T>;
        }
    }

    /// <summary>
    /// Puts a small color square before each color value in a .xaml file. A literal color is known from
    /// the text. A key ({StaticResource AccentBrush}) is looked up in the index on a background thread,
    /// and the swatch appears when the answer is there.
    /// </summary>
    internal sealed class ColorSwatchTagger : ITagger<IntraTextAdornmentTag>, IDisposable
    {
        private const int SwatchSize = 10;

        private static readonly Brush Checkerboard = CreateCheckerboard();

        private readonly ITextView _view;
        private readonly ITextBuffer _buffer;
        private readonly ITextDocument _document;
        private readonly JoinableTaskFactory _joinableTaskFactory;
        private readonly ResourceIndex _index;

        // Key → color, or null when the key is no simple color. Read and written on the UI thread only.
        private readonly Dictionary<string, Color?> _colors = new Dictionary<string, Color?>(StringComparer.Ordinal);
        private readonly HashSet<string> _stale = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _queued = new HashSet<string>(StringComparer.Ordinal);
        private bool _lookupRunning;
        private CancellationTokenSource _staleDelay;

        public ColorSwatchTagger(ITextView view, ITextBuffer buffer, ITextDocument document, JoinableTaskFactory joinableTaskFactory)
        {
            _view = view;
            _buffer = buffer;
            _document = document;
            _joinableTaskFactory = joinableTaskFactory;
            _index = ResourceIndex.ForFile(document.FilePath);

            _buffer.Changed += OnBufferChanged;
            _index.FilesChanged += OnFilesChanged;
            SettingsService.Changed += OnSettingsChanged;
            _view.Closed += (s, e) => Dispose();
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0 || !SettingsService.Current.ShowColorSwatches)
            {
                yield break;
            }

            ITextSnapshot snapshot = spans[0].Snapshot;
            int lastLine = -1;
            foreach (SnapshotSpan span in spans)
            {
                int first = Math.Max(span.Start.GetContainingLine().LineNumber, lastLine + 1);
                int last = span.End.GetContainingLine().LineNumber;
                for (int number = first; number <= last; number++)
                {
                    ITextSnapshotLine line = snapshot.GetLineFromLineNumber(number);
                    foreach (SwatchSpot spot in ColorSwatchFinder.Find(line.GetText()))
                    {
                        Color? color = spot.Color ?? Lookup(spot.Key);
                        if (color.HasValue)
                        {
                            var position = new SnapshotSpan(snapshot, line.Start.Position + spot.Position, 0);
                            yield return new TagSpan<IntraTextAdornmentTag>(
                                position,
                                new IntraTextAdornmentTag(CreateSwatch(color.Value, spot.Key), null, PositionAffinity.Successor));
                        }
                    }
                }

                lastLine = last;
            }
        }

        /// <summary>The known color of a key, and queues a lookup when it is unknown or may have changed.</summary>
        private Color? Lookup(string key)
        {
            bool known = _colors.TryGetValue(key, out Color? color);
            if (!known || _stale.Contains(key))
            {
                _queued.Add(key);
                StartLookup();
            }

            return color; // a stale color stays visible until the new one is there, so nothing flickers
        }

        private void StartLookup()
        {
            if (_lookupRunning || _queued.Count == 0)
            {
                return;
            }

            _lookupRunning = true;
            string[] keys = _queued.ToArray();
            _queued.Clear();
            foreach (string key in keys)
            {
                _stale.Remove(key);
            }

            string filePath = _document.FilePath;
            string text = _buffer.CurrentSnapshot.GetText();
            _joinableTaskFactory.RunAsync(async () =>
            {
                // The first lookup in a solution reads every .xaml file. Keep that off the UI thread.
                await TaskScheduler.Default;
                var found = new Dictionary<string, Color?>(StringComparer.Ordinal);
                try
                {
                    _index.SetLiveText(filePath, text);
                    foreach (string key in keys)
                    {
                        found[key] = ColorSwatchFinder.Resolve(_index, key, filePath, CancellationToken.None);
                    }
                }
                finally
                {
                    await _joinableTaskFactory.SwitchToMainThreadAsync();
                    _lookupRunning = false;
                }

                bool changed = false;
                foreach (KeyValuePair<string, Color?> pair in found)
                {
                    if (!_colors.TryGetValue(pair.Key, out Color? old) || old != pair.Value)
                    {
                        _colors[pair.Key] = pair.Value;
                        changed = true;
                    }
                }

                if (changed)
                {
                    RaiseTagsChanged();
                }

                StartLookup(); // keys that came in while this lookup ran
            }).FileAndForget("ResourcePreview/ColorSwatches");
        }

        // --- When colors may change ---

        /// <summary>
        /// An edit can change a definition in this file. Mark every key as stale after a short pause, so
        /// typing does not start a lookup per key stroke; the visible lines then ask again.
        /// </summary>
        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            _staleDelay?.Cancel();
            _staleDelay = new CancellationTokenSource();
            CancellationToken token = _staleDelay.Token;
            _joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await Task.Delay(500, token);
                }
                catch (OperationCanceledException)
                {
                    return; // another edit came in
                }

                await _joinableTaskFactory.SwitchToMainThreadAsync();
                MarkAllStale();
            }).FileAndForget("ResourcePreview/ColorSwatches");
        }

        private void OnFilesChanged(object sender, EventArgs e)
        {
            _joinableTaskFactory.RunAsync(async () =>
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                MarkAllStale();
            }).FileAndForget("ResourcePreview/ColorSwatches");
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            _joinableTaskFactory.RunAsync(async () =>
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                RaiseTagsChanged();
            }).FileAndForget("ResourcePreview/ColorSwatches");
        }

        private void MarkAllStale()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_colors.Count == 0)
            {
                return;
            }

            _stale.UnionWith(_colors.Keys);
            RaiseTagsChanged();
        }

        private void RaiseTagsChanged()
        {
            ITextSnapshot snapshot = _buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        // --- The swatch ---

        /// <summary>A square with a gray border, readable in light and dark themes. Transparent colors show a checkerboard.</summary>
        private FrameworkElement CreateSwatch(Color color, string key)
        {
            var fill = new Border { Background = new SolidColorBrush(color) };
            var swatch = new Border
            {
                Width = SwatchSize,
                Height = SwatchSize,
                Margin = new Thickness(1, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Background = color.A < 255 ? Checkerboard : null,
                Child = fill,
                ToolTip = key == null ? color.ToString() : key + "  " + color,
                SnapsToDevicePixels = true,
            };

            // The editor scales text with the zoom level; keep the square in proportion.
            double scale = _view.LineHeight > 0 ? Math.Min(1.6, Math.Max(0.8, _view.LineHeight / 16)) : 1;
            swatch.Width = swatch.Height = Math.Round(SwatchSize * scale);
            return swatch;
        }

        private static Brush CreateCheckerboard()
        {
            var drawing = new GeometryDrawing(
                Brushes.LightGray,
                null,
                new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 2, 2)), new RectangleGeometry(new Rect(2, 2, 2, 2)) } });
            var brush = new DrawingBrush(new DrawingGroup { Children = { new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 4, 4))), drawing } })
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 4, 4),
                ViewportUnits = BrushMappingMode.Absolute,
            };
            brush.Freeze();
            return brush;
        }

        public void Dispose()
        {
            _buffer.Changed -= OnBufferChanged;
            _index.FilesChanged -= OnFilesChanged;
            SettingsService.Changed -= OnSettingsChanged;
            _staleDelay?.Cancel();
        }
    }
}
