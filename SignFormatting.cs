using System.Text.Json;

namespace SignFonts;

// Plain text remains in vanilla's packets and save data. These ranges only describe
// how to draw it; no markup is inserted into players' sign wording.
internal sealed record LetterStyle(string Family = null, int Size = 0, bool? Bold = null, bool? Italic = null, bool Underline = false);
internal sealed record LetterRun(int Start, int Length, LetterStyle Style);

internal sealed class FormattedField
{
    public string Text { get; set; } = "";
    public string Alignment { get; set; } = "center";
    public Dictionary<int, string> LineAlignments { get; set; } = new();
    public List<LetterRun> Runs { get; set; } = new();

    internal int LineAt(int position) => Text.Take(Math.Clamp(position, 0, Text.Length)).Count(c => c == '\n');
    internal string AlignmentAtLine(int line) => LineAlignments.TryGetValue(line, out var alignment) ? alignment : Alignment;

    internal void AlignSelection(int start, int end, string alignment)
    {
        start = Math.Clamp(start, 0, Text.Length);
        end = Math.Clamp(end, start, Text.Length);
        var first = LineAt(start);
        // A selection ending at the next line's start does not include that line.
        var last = LineAt(end > start ? end - 1 : start);
        for (var line = first; line <= last; line++)
        {
            if (alignment == Alignment) LineAlignments.Remove(line);
            else LineAlignments[line] = alignment;
        }
    }

    internal LetterStyle[] Expand()
    {
        var styles = Enumerable.Repeat(new LetterStyle(), Text.Length).ToArray();
        foreach (var run in Runs)
            Array.Fill(styles, run.Style, run.Start, run.Length);
        return styles;
    }

    internal void Pack(LetterStyle[] styles)
    {
        Runs.Clear();
        for (var i = 0; i < styles.Length;)
        {
            var end = i + 1;
            while (end < styles.Length && styles[end] == styles[i]) end++;
            if (styles[i] != new LetterStyle()) Runs.Add(new LetterRun(i, end - i, styles[i]));
            i = end;
        }
    }

    internal void Reconcile(string next)
    {
        next ??= "";
        if (next == Text) return;
        var old = Expand();
        var prefix = 0;
        while (prefix < Text.Length && prefix < next.Length && Text[prefix] == next[prefix]) prefix++;
        var suffix = 0;
        while (suffix < Text.Length - prefix && suffix < next.Length - prefix && Text[Text.Length - 1 - suffix] == next[next.Length - 1 - suffix]) suffix++;
        var nextAlignments = new Dictionary<int, string>();
        var lineIndex = 0;
        for (var position = 0; position <= next.Length; position++)
        {
            if (position > 0 && next[position - 1] != '\n') continue;
            // Unchanged lines retain their alignment; inserted lines inherit the
            // paragraph being edited. A merged line keeps its leading alignment.
            var oldPosition = position <= prefix ? position
                : position >= next.Length - suffix ? position + Text.Length - next.Length : prefix;
            var alignment = AlignmentAtLine(LineAt(oldPosition));
            if (alignment != Alignment) nextAlignments[lineIndex] = alignment;
            lineIndex++;
        }
        var inherit = old.Length == 0 ? new LetterStyle() : old[Math.Min(prefix, old.Length - 1)];
        var result = Enumerable.Repeat(inherit, next.Length).ToArray();
        Array.Copy(old, result, prefix);
        Array.Copy(old, old.Length - suffix, result, next.Length - suffix, suffix);
        Text = next;
        LineAlignments = nextAlignments;
        Pack(result);
    }

    internal void Format(int start, int end, Func<LetterStyle, LetterStyle> change)
    {
        var styles = Expand();
        start = Math.Clamp(start, 0, styles.Length);
        end = Math.Clamp(end, start, styles.Length);
        for (var i = start; i < end; i++) styles[i] = change(styles[i]);
        Pack(styles);
    }
}

internal sealed class SignFormatting
{
    public List<FormattedField> Fields { get; set; } = new();
    internal bool HasFormatting => Fields.Any(f => f.Runs.Count > 0 || f.Alignment != "center" || f.LineAlignments.Count > 0);
    internal SignFormatting Clone() => Parse(JsonSerializer.Serialize(this)) ?? new SignFormatting();
    internal string Serialize() => HasFormatting ? JsonSerializer.Serialize(this) : "";

    internal static SignFormatting Parse(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        if (json.Length > 131072) throw new ArgumentException("Formatting is too large.");
        var doc = JsonSerializer.Deserialize<SignFormatting>(json) ?? throw new ArgumentException("Missing formatting.");
        if (doc.Fields == null || doc.Fields.Count is < 1 or > 8) throw new ArgumentException("Invalid sign fields.");
        var total = 0;
        foreach (var field in doc.Fields)
        {
            if (field?.Text == null || field.Text.Length > 8192 || (total += field.Text.Length) > 16384 || field.Runs == null || field.Runs.Count > 2048)
                throw new ArgumentException("Invalid sign text ranges.");
            if (field.Alignment != "left" && field.Alignment != "center" && field.Alignment != "right") throw new ArgumentException("Invalid alignment.");
            var lastLine = field.Text.Count(c => c == '\n');
            if (field.LineAlignments == null || field.LineAlignments.Any(pair => pair.Key < 0 || pair.Key > lastLine || (pair.Value != "left" && pair.Value != "center" && pair.Value != "right")))
                throw new ArgumentException("Invalid line alignment.");
            var previousEnd = 0;
            foreach (var run in field.Runs)
            {
                if (run?.Style == null || run.Start < previousEnd || run.Length <= 0 || run.Start > field.Text.Length - run.Length)
                    throw new ArgumentException("Invalid formatting range.");
                var style = run.Style;
                if ((style.Size != 0 && (style.Size < 8 || style.Size > 72)) || style.Family?.Length > 128 || style.Family?.Any(char.IsControl) == true)
                    throw new ArgumentException("Invalid text style.");
                previousEnd = run.Start + run.Length;
            }
        }
        return doc;
    }
}
