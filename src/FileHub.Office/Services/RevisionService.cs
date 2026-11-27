using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using FileHub.Office.Models;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace FileHub.Office.Services;

/// <summary>
/// Word tracked changes: reads w:ins / w:del / w:rPrChange, records new edits with a word-level
/// diff, writes pending revisions back to OOXML and implements accept / reject.
/// </summary>
public static class RevisionService
{
    private static int _nextId = 1;

    /// <summary>Author written into new revision metadata.</summary>
    public static string Author { get; set; } =
        string.IsNullOrWhiteSpace(Environment.UserName) ? "FileHub" : Environment.UserName;

    private static string Stamp() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    /// <summary>Loads tracked changes from the original package into the model.</summary>
    public static void Read(OfficeDocument doc, MainDocumentPart main)
    {
        var body = main.Document?.Body;
        if (body is null) return;
        var paragraphs = doc.Paragraphs.ToList();

        var index = 0;
        foreach (var element in body.Elements<W.Paragraph>())
        {
            var target = index < paragraphs.Count ? paragraphs[index] : null;

            foreach (var inserted in element.Elements<W.InsertedRun>())
            {
                var text = string.Concat(inserted.Descendants<W.Text>().Select(t => t.Text ?? string.Empty));
                if (text.Length == 0) continue;
                var id = NextId();
                doc.Revisions.Add(new WordRevision
                {
                    Id = id,
                    Kind = WordRevisionKind.Insertion,
                    Author = inserted.Author?.Value ?? Author,
                    Date = ParseDate(inserted.Date?.Value),
                    Text = text,
                    ParagraphIndex = index
                });
                if (target is not null)
                {
                    var run = target.Runs.FirstOrDefault()?.Clone() ?? new WordRun();
                    run.Text = text;
                    run.RevisionId = id;
                    run.ColorHex = "008000";
                    run.IsUnderline = true;
                    target.Runs.Add(run);
                }
            }

            foreach (var deleted in element.Elements<W.DeletedRun>())
            {
                var text = string.Concat(deleted.Descendants<W.DeletedText>().Select(t => t.Text ?? string.Empty));
                if (text.Length == 0) continue;
                doc.Revisions.Add(new WordRevision
                {
                    Id = NextId(),
                    Kind = WordRevisionKind.Deletion,
                    Author = deleted.Author?.Value ?? Author,
                    Date = ParseDate(deleted.Date?.Value),
                    Text = text,
                    ParagraphIndex = index
                });
            }

            foreach (var moveFrom in element.Elements<W.MoveFromRun>())
                AddMoveRevision(doc, moveFrom, WordRevisionKind.MoveFrom, index);
            foreach (var moveTo in element.Elements<W.MoveToRun>())
                AddMoveRevision(doc, moveTo, WordRevisionKind.MoveTo, index);

            foreach (var change in element.Descendants<W.RunPropertiesChange>())
            {
                doc.Revisions.Add(new WordRevision
                {
                    Id = NextId(),
                    Kind = WordRevisionKind.FormatChange,
                    Author = change.Author?.Value ?? Author,
                    Date = ParseDate(change.Date?.Value),
                    Text = string.Empty,
                    FormatSummary = change.PreviousRunProperties?.InnerXml ?? "format",
                    ParagraphIndex = index
                });
            }
            index++;
        }
        _nextId = Math.Max(_nextId, doc.Revisions.Count + 1);
    }

    private static void AddMoveRevision(OfficeDocument doc, W.RunTrackChangeType element, WordRevisionKind kind, int index)
    {
        var text = string.Concat(element.Descendants<W.Text>().Select(t => t.Text ?? string.Empty));
        if (text.Length == 0) return;
        doc.Revisions.Add(new WordRevision
        {
            Id = NextId(),
            Kind = kind,
            Author = element.Author?.Value ?? Author,
            Date = ParseDate(element.Date?.Value),
            Text = text,
            ParagraphIndex = index
        });
    }

    private static DateTimeOffset? ParseDate(DateTime? value) =>
        value is { } date ? new DateTimeOffset(date.ToUniversalTime()) : null;

    private static int NextId() => _nextId++;

    /// <summary>Records a user edit as tracked insertions/deletions when tracking is on.</summary>
    public static void RecordChange(OfficeDocument doc, int paragraphIndex, string oldText, string newText)
    {
        if (!doc.TrackChangesEnabled || oldText == newText) return;
        var paragraph = doc.Paragraphs.ElementAtOrDefault(paragraphIndex);
        if (paragraph is null) return;

        var oldTokens = Tokenize(oldText);
        var newTokens = Tokenize(newText);
        var (removed, added) = Diff(oldTokens, newTokens);
        if (removed.Count == 0 && added.Count == 0) return;

        var leader = paragraph.Runs.FirstOrDefault()?.Clone() ?? new WordRun();
        leader.RevisionId = 0;
        leader.ColorHex = string.Empty;
        leader.IsUnderline = false;

        var keep = new HashSet<int>(Enumerable.Range(0, oldTokens.Count).Except(removed.Keys));
        var keptText = string.Concat(oldTokens.Where((_, i) => keep.Contains(i)));
        paragraph.Runs.Clear();
        if (keptText.Length > 0)
        {
            var unchanged = leader.Clone();
            unchanged.Text = keptText;
            paragraph.Runs.Add(unchanged);
        }

        if (added.Count > 0)
        {
            var id = NextId();
            var inserted = leader.Clone();
            inserted.Text = string.Concat(added.Values);
            inserted.RevisionId = id;
            inserted.ColorHex = "008000";
            inserted.IsUnderline = true;
            paragraph.Runs.Add(inserted);
            doc.Revisions.Add(new WordRevision
            {
                Id = id, Kind = WordRevisionKind.Insertion, Author = Author, Date = DateTimeOffset.UtcNow,
                Text = inserted.Text, ParagraphIndex = paragraphIndex
            });
        }

        if (removed.Count > 0)
        {
            var id = NextId();
            doc.Revisions.Add(new WordRevision
            {
                Id = id, Kind = WordRevisionKind.Deletion, Author = Author, Date = DateTimeOffset.UtcNow,
                Text = string.Concat(removed.Values), ParagraphIndex = paragraphIndex
            });
        }
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = string.Empty;
        var isWord = false;
        foreach (var c in text)
        {
            var word = char.IsLetterOrDigit(c) || c == '_';
            if (current.Length > 0 && word != isWord)
            {
                tokens.Add(current);
                current = string.Empty;
            }
            isWord = word;
            current += c;
        }
        if (current.Length > 0) tokens.Add(current);
        return tokens;
    }

    private static (Dictionary<int, string> Removed, Dictionary<int, string> Added) Diff(
        IReadOnlyList<string> oldTokens, IReadOnlyList<string> newTokens)
    {
        // Cap the table so a pasted book does not allocate an enormous matrix.
        var a = oldTokens.Take(240).ToList();
        var b = newTokens.Take(240).ToList();
        var table = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
            for (var j = b.Count - 1; j >= 0; j--)
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);

        var removed = new Dictionary<int, string>();
        var added = new Dictionary<int, string>();
        var x = 0;
        var y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y]) { x++; y++; }
            else if (table[x + 1, y] >= table[x, y + 1]) removed[x++] = a[x - 1];
            else added[y++] = b[y - 1];
        }
        while (x < a.Count) removed[x++] = a[x - 1];
        while (y < b.Count) added[y++] = b[y - 1];
        return (removed, added);
    }

    /// <summary>Writes pending insertions (run markers) and deletions into the package.</summary>
    public static void Write(OfficeDocument doc, MainDocumentPart main)
    {
        var body = main.Document?.Body;
        if (body is null) return;
        var paragraphs = body.Elements<W.Paragraph>().ToList();
        foreach (var revision in doc.Revisions.Where(r => r.Kind == WordRevisionKind.Deletion).ToList())
        {
            if (revision.ParagraphIndex < 0 || revision.ParagraphIndex >= paragraphs.Count) continue;
            var deleted = new W.DeletedRun
            {
                Author = revision.Author,
                Date = new DocumentFormat.OpenXml.DateTimeValue((revision.Date ?? DateTimeOffset.UtcNow).UtcDateTime),
                Id = revision.Id.ToString()
            };
            deleted.AppendChild(new W.Run(new W.DeletedText
            {
                Text = revision.Text,
                Space = SpaceProcessingModeValues.Preserve
            }));
            paragraphs[revision.ParagraphIndex].AppendChild(deleted);
        }
    }

    /// <summary>Accepts one revision, applying its result to the editable model.</summary>
    public static void Accept(OfficeDocument doc, WordRevision revision)
    {
        if (revision.Kind == WordRevisionKind.Insertion)
        {
            foreach (var run in ParagraphAt(doc, revision.ParagraphIndex)?.Runs.Where(r => r.RevisionId == revision.Id) ?? Enumerable.Empty<WordRun>())
                run.RevisionId = 0;
        }
        // Deletions and format changes are already reflected once accepted.
        doc.Revisions.Remove(revision);
    }

    /// <summary>Rejects one revision, restoring the text that existed before the change.</summary>
    public static void Reject(OfficeDocument doc, WordRevision revision)
    {
        var paragraph = ParagraphAt(doc, revision.ParagraphIndex);
        if (paragraph is not null)
        {
            if (revision.Kind is WordRevisionKind.Insertion or WordRevisionKind.MoveTo)
            {
                foreach (var run in paragraph.Runs.Where(r => r.RevisionId == revision.Id).ToList())
                    paragraph.Runs.Remove(run);
            }
            else if (!string.IsNullOrEmpty(revision.Text))
            {
                var restored = paragraph.Runs.FirstOrDefault()?.Clone() ?? new WordRun();
                restored.RevisionId = 0;
                restored.ColorHex = string.Empty;
                restored.IsUnderline = false;
                restored.Text = revision.Text;
                paragraph.Runs.Add(restored);
            }
        }
        doc.Revisions.Remove(revision);
    }

    public static void AcceptAll(OfficeDocument doc)
    {
        foreach (var revision in doc.Revisions.ToList()) Accept(doc, revision);
    }

    public static void RejectAll(OfficeDocument doc)
    {
        foreach (var revision in doc.Revisions.OrderByDescending(r => r.Id).ToList()) Reject(doc, revision);
    }

    private static WordParagraph? ParagraphAt(OfficeDocument doc, int index) =>
        doc.Paragraphs.ElementAtOrDefault(index);
}
