namespace ResourcePreview.Resources
{
    /// <summary>Replace <see cref="Length"/> characters at <see cref="Start"/> with <see cref="NewText"/>. Length 0 inserts.</summary>
    internal readonly struct TextChange
    {
        public TextChange(int start, int length, string newText)
        {
            Start = start;
            Length = length;
            NewText = newText;
        }

        public int Start { get; }

        public int Length { get; }

        public string NewText { get; }

        /// <summary>Applies changes to a string, the way the editor does. Used by the tests.</summary>
        public static string Apply(string text, System.Collections.Generic.IEnumerable<TextChange> changes)
        {
            var sorted = new System.Collections.Generic.List<TextChange>(changes);
            sorted.Sort((a, b) => b.Start.CompareTo(a.Start));
            foreach (TextChange change in sorted)
            {
                text = text.Substring(0, change.Start) + change.NewText + text.Substring(change.Start + change.Length);
            }

            return text;
        }
    }
}
