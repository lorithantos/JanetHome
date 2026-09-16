using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Janet.Core;

/// <summary>
/// What one automatic archive moved out of an item's notes, exactly as the write envelope
/// reports it under 'split'.
/// </summary>
/// <remarks>
/// Present in the envelope ONLY when an archive happened, matching the 'notesTruncated'
/// precedent: an absent key means there is nothing to go back for. 'where' is the field a
/// caller acts on -- an absolute path in this design -- and 'kind' says what sort of thing it
/// names, so a consumer written today still parses when a second destination is added.
/// </remarks>
public sealed record NotesArchive(
    int Archived,
    int Retained,
    string Boundary,
    int Paragraphs,
    string Kind,
    string Where,
    int Generation,
    string Header);

/// <summary>
/// One archive, computed but not yet on disk.
/// </summary>
/// <remarks>
/// <see cref="Composed"/> is the note text the plan was computed FROM, and it is what makes the
/// plan safe to apply inside the write queue: the queued operation recomposes the append against
/// the text the batch actually read, and applies this plan only if the two agree. A concurrent
/// writer that changed the item in between leaves the plan stale, and a stale plan is not
/// applied -- the append then follows the ordinary path, which refuses if it is over the ceiling.
/// </remarks>
public sealed record NotesArchivePlan(
    string Composed,
    string Notes,
    string FileText,
    NotesArchive Archive);

/// <summary>
/// Moves the OLDEST part of an over-ceiling note to a file beside the store and leaves a pointer
/// behind.
/// </summary>
/// <remarks>
/// Automatic means ARCHIVE, not SPLIT. A split into two live topics needs a NAME, a topic here
/// is an identity that no verb can change afterwards, and a name a machine derives from "the
/// first 6,142 characters of an append log" is a false label forever. An archive is reached
/// through a pointer rather than by browsing, so an opaque identifier is not merely acceptable
/// there, it is correct. The manual split stays unbuilt and available.
///
/// WHAT STAYS LIVE IS THE NEWEST. These notes are append-only working logs: Update composes
/// old + blank line + new, and 'next' -- the resume cursor -- points at the present, so the
/// notes that support it are the recent ones. The cost of keeping the tail is that the first
/// line changes, and that cost is paid deliberately: the retained text opens with a pointer
/// header, which makes it the notesLead in every report and in the startup brief. A caller that
/// never reads the write envelope still finds the text.
///
/// THE NAME IS CONTENT-ADDRESSED: slug of the topic, the date, and the first 8 hex of a
/// SHA-256 of the archived text. There is no ordinal to probe and therefore no collision to
/// lose; two sessions archiving the same text write the same bytes, and the second and third
/// archive of one item produce a second and third file, each header naming its predecessor so
/// the chain is walkable backwards.
///
/// THE FILE IS WRITTEN BEFORE THE QUEUE, never inside a queued operation -- see
/// <see cref="WriteQueue.Submit"/>, whose operations must be pure with respect to disk because
/// in-process writers coalesce into one read-apply-write and a discarded operation's text must
/// not leave a side effect behind. This is NOT a two-phase commit and the trade was accepted
/// knowingly: a failed file write queues nothing and leaves the item untouched, and a failed
/// queued write leaves an orphan .md file and an untouched item. No text is ever lost and no
/// half-archive is observable through the API; the residue of a failure is a file with no
/// pointer, which a retry re-creates byte-identically because the name is content-addressed.
/// </remarks>
public static class NotesSplit
{
    /// <summary>The destination kind this design writes. 'item' and 'note' are the shapes not built.</summary>
    public const string FileKind = "file";

    /// <summary>The separator <see cref="ThreadItems.Update"/> itself writes between appends.</summary>
    public const string ParagraphBoundary = "paragraph";

    /// <summary>The fallback boundary: a single newline, still never mid-sentence.</summary>
    public const string LineBoundary = "line";

    /// <summary>Longest a topic slug may be in a file name.</summary>
    private const int SlugLength = 60;

    private const string HeaderPrefix = "ARCHIVED ";

    private const string HeaderMoved = " moved to ";

    private const string HeaderTail = " -- ";

    /// <summary>Where an archive lands: a 'notes' directory beside the store itself.</summary>
    /// <remarks>
    /// The store, not the repo. This list is shared by every repo on this machine, so a repo's
    /// notes directory is the wrong home for another project's prose; following the store means
    /// a test passing its own --path gets its own archives, and that when the store moves out of
    /// %TEMP% the archives move with it.
    /// </remarks>
    public static string ArchiveDirectory(string storePath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(storePath)) ?? Directory.GetCurrentDirectory(),
            "notes");

    /// <summary>The topic as a file-name fragment: lowercased, runs of anything else collapsed.</summary>
    public static string Slug(string topic)
    {
        StringBuilder builder = new();
        bool pending = false;

        foreach (char c in topic)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pending && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(c));
                pending = false;
            }
            else
            {
                pending = true;
            }

            if (builder.Length >= SlugLength)
            {
                break;
            }
        }

        string slug = builder.ToString().Trim('-');

        return slug.Length > 0 ? slug : "thread-item";
    }

    /// <summary>The first 8 hex of a SHA-256 over the text being archived.</summary>
    public static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];

    /// <summary>
    /// The archive path named by a note's own pointer header, or null when it carries none.
    /// </summary>
    /// <remarks>
    /// This is how the chain is walked backwards: the live notes name the newest archive, and
    /// each archived file names the one before it. Parsing our own format rather than storing a
    /// second field, because the header has to be prose a human reads anyway.
    /// </remarks>
    public static string? PointerIn(string notes)
    {
        string? line = notes
            .Replace("\r\n", "\n")
            .Split('\n')
            .FirstOrDefault(l => l.Trim().Length > 0);

        if (line is null || !line.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        int from = line.IndexOf(HeaderMoved, StringComparison.Ordinal);

        if (from < 0)
        {
            return null;
        }

        from += HeaderMoved.Length;

        int to = line.IndexOf(HeaderTail, from, StringComparison.Ordinal);

        return to < 0 ? null : line[from..to];
    }

    /// <summary>
    /// How many times this item has already archived, read off the directory rather than stored.
    /// </summary>
    /// <remarks>
    /// Derived, because the alternative is a field on the item and the item's shape is an
    /// allow-list that two serializers have to agree about. Two topics that slug identically
    /// would share a count; they would not share a file, since the hash is over the text.
    /// </remarks>
    public static int ArchiveCount(string directory, string slug)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        Regex shape = new(
            "^" + Regex.Escape(slug) + "-[0-9]{8}-[0-9a-f]{8}\\.md$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return Directory.EnumerateFiles(directory, slug + "-*.md")
            .Count(f => shape.IsMatch(Path.GetFileName(f)));
    }

    /// <summary>
    /// Plans the archive for a composed note that is over the ceiling, or returns null when no
    /// boundary falls in the archivable range.
    /// </summary>
    /// <param name="storePath">The thread store this item lives in; the archive lands beside it.</param>
    /// <param name="topic">The item's topic, which becomes the file-name slug and the provenance.</param>
    /// <param name="composed">The notes the item WOULD hold: the existing text plus the append.</param>
    /// <param name="protectedTail">
    /// Characters at the end that may never be cut, the blank line that joins them included --
    /// the fragment the caller sent in this very call. Splitting text the caller just wrote
    /// would put one authored passage in two places, and it is why an item whose stored notes
    /// are ONE block is still refused whole: the only boundary such a note has is the one this
    /// append just created, and cutting there is cutting at the caller's own text.
    /// </param>
    /// <param name="ceiling">The ceiling in force. Retention is half of it.</param>
    /// <param name="today">The date the header and the file name carry.</param>
    /// <remarks>
    /// The cut keeps the LARGEST tail that still fits the retention target, so the live item is
    /// left with as much of the present as the target allows and the next append does not
    /// immediately archive again. Archiving to the ceiling rather than to half of it is the
    /// failure this avoids: ten files where one was wanted.
    ///
    /// Paragraphs first, single newlines second, and nothing else ever. A cut that is neither is
    /// a cut inside a sentence, and no boundary in range means the write is refused whole --
    /// which is what makes a single 9,000-character paragraph behave exactly as it did before
    /// any of this existed.
    /// </remarks>
    public static NotesArchivePlan? Plan(
        string storePath, string topic, string composed, int protectedTail, int ceiling,
        DateOnly today)
    {
        int retention = NotesBudget.Retention(ceiling);
        string directory = ArchiveDirectory(storePath);
        string slug = Slug(topic);
        int generation = ArchiveCount(directory, slug) + 1;
        string? previous = PointerIn(composed);

        return Attempt(composed, "\n\n", ParagraphBoundary, protectedTail, retention, directory, slug, today, generation, previous, topic, storePath)
            ?? Attempt(composed, "\n", LineBoundary, protectedTail, retention, directory, slug, today, generation, previous, topic, storePath);
    }

    /// <summary>One pass over the candidate cuts of a single separator, earliest first.</summary>
    /// <remarks>
    /// Earliest first because the tail shrinks as the cut moves later: the first cut whose
    /// retained text fits the retention target is the one that keeps the most of the present.
    /// The header's own length is inside that measurement, and the header names the archived
    /// size and the content hash, so each candidate is costed with the header it would actually
    /// carry rather than with an estimate.
    /// </remarks>
    private static NotesArchivePlan? Attempt(
        string composed, string separator, string boundary, int protectedTail, int retention,
        string directory, string slug, DateOnly today, int generation, string? previous,
        string topic, string storePath)
    {
        int last = composed.Length - protectedTail - separator.Length;

        for (int at = composed.IndexOf(separator, StringComparison.Ordinal);
             at >= 0 && at <= last;
             at = composed.IndexOf(separator, at + 1, StringComparison.Ordinal))
        {
            string archived = composed[..at];

            if (archived.Trim().Length == 0)
            {
                continue;
            }

            string tail = composed[(at + separator.Length)..];
            int paragraphs = Paragraphs(archived);
            string path = Path.Combine(
                directory,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{slug}-{today:yyyyMMdd}-{Hash(archived)}.md"));

            string header = Header(today, archived.Length, path, paragraphs);
            string notes = header + "\n\n" + tail;

            if (notes.Length > retention)
            {
                continue;
            }

            return new NotesArchivePlan(
                composed,
                notes,
                FileText(topic, storePath, today, archived, paragraphs, previous),
                new NotesArchive(
                    archived.Length, notes.Length, boundary, paragraphs, FileKind, path,
                    generation, header));
        }

        return null;
    }

    /// <summary>
    /// The smallest retained tail any legal cut could leave, or -1 when there is no legal cut.
    /// </summary>
    /// <remarks>
    /// What a refusal needs in order to say WHICH thing went wrong. -1 is prose with nowhere to
    /// cut -- one block, refused exactly as it was before archiving existed. A number is prose
    /// that has boundaries but none that helps, because the newest block plus the text just sent
    /// is itself larger than the retention target, and that text is never cut.
    /// </remarks>
    public static int SmallestRetained(string composed, int protectedTail)
    {
        int paragraph = Smallest(composed, "\n\n", protectedTail);
        int line = Smallest(composed, "\n", protectedTail);

        return paragraph >= 0 && line >= 0 ? Math.Min(paragraph, line) : Math.Max(paragraph, line);
    }

    private static int Smallest(string composed, string separator, int protectedTail)
    {
        int last = composed.Length - protectedTail - separator.Length;
        int found = -1;

        for (int at = composed.IndexOf(separator, StringComparison.Ordinal);
             at >= 0 && at <= last;
             at = composed.IndexOf(separator, at + 1, StringComparison.Ordinal))
        {
            if (composed[..at].Trim().Length > 0)
            {
                found = composed.Length - at - separator.Length;
            }
        }

        return found;
    }

    /// <summary>Blank-line separated blocks with anything in them.</summary>
    private static int Paragraphs(string archived) =>
        archived.Split("\n\n", StringSplitOptions.None).Count(b => b.Trim().Length > 0);

    /// <summary>
    /// The pointer line the retained notes open with.
    /// </summary>
    /// <remarks>
    /// ONE LINE, deliberately: the notesLead is the first non-empty line, so a header wrapped
    /// over three lines would publish "ARCHIVED 2026-09-16: the first 6,142 characters of these
    /// notes moved to" and leave the path out of every report that matters.
    /// </remarks>
    public static string Header(DateOnly date, int archived, string path, int paragraphs) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1:yyyy-MM-dd}: the first {2:N0} characters of these notes{3}{4}{5}{6} " +
            "{7}, the oldest first. This log continues below.",
            HeaderPrefix,
            date,
            archived,
            HeaderMoved,
            path,
            HeaderTail,
            paragraphs,
            paragraphs == 1 ? "paragraph" : "paragraphs");

    /// <summary>The archive file: a provenance block, then the moved text verbatim.</summary>
    /// <remarks>
    /// The archived text is NEVER re-scanned by the malformed-markup guard. It is text that is
    /// already stored, some of it legitimately quoting the tool-call markup that guard refuses --
    /// the item tracking the guard among them -- and it moves exactly as it was.
    ///
    /// The provenance carries no generation number on purpose: two sessions archiving the same
    /// text must produce the same bytes, and a count read off a directory is the one part of
    /// this that a race can disagree about.
    /// </remarks>
    private static string FileText(
        string topic, string storePath, DateOnly today, string archived, int paragraphs,
        string? previous)
    {
        StringBuilder builder = new();

        builder.Append("ARCHIVED FROM THREAD ITEM: ").Append(topic).Append('\n');
        builder.Append(string.Format(
            CultureInfo.InvariantCulture,
            "Archived {0:yyyy-MM-dd} from {1}. {2:N0} characters, {3} {4}, oldest first.\n",
            today,
            Path.GetFullPath(storePath),
            archived.Length,
            paragraphs,
            paragraphs == 1 ? "paragraph" : "paragraphs"));

        if (previous is not null)
        {
            builder.Append("Previous archive: ").Append(previous).Append('\n');
        }

        builder.Append('\n').Append(archived).Append('\n');

        return builder.ToString();
    }

    /// <summary>
    /// Writes the archive file. Called BEFORE anything is queued, and never from inside a
    /// queued operation.
    /// </summary>
    /// <remarks>
    /// A file that is already there is left alone rather than rewritten: the name is a hash of
    /// the contents, so an existing one holds the same bytes, and skipping the write makes a
    /// retry after a stale plan free instead of racing a reader.
    /// </remarks>
    public static void Commit(NotesArchivePlan plan)
    {
        string path = plan.Archive.Where;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (!File.Exists(path))
            {
                NodeText.WriteUtf8NoBom(path, plan.FileText);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GraphException(
                $"Could not write the notes archive to {path}: {ex.Message} Nothing was " +
                "written to the thread item either, so the notes are exactly as they were. " +
                "Retry, or pass split=false to be refused the old way.",
                ex);
        }
    }
}
