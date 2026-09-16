using System.Globalization;

namespace Janet.Core;

/// <summary>
/// How large a thread item's notes may be before a write is refused, and the fixed size of its
/// resume cursor. Shared by every front end so they cannot disagree about the number.
/// </summary>
/// <remarks>
/// The list stores notes INLINE, one JSON file for every item on this machine, and until
/// 2026-09-05 nothing bounded them: the live list carried items of 2,000 to 3,000 characters
/// and the store as a whole was over 200KB, which is what made an unnarrowed thread_show exceed
/// the result budget in the first place. The result budget refuses the READ; this refuses the
/// WRITE that would make the next read unreadable, which is the earlier and cheaper place.
///
/// THE NUMBER IS CHOSEN. 8,000 characters is about the size of a startup brief, and an item
/// that needs more is no longer a working log -- it is a note, and notes have a home: a file
/// under notes\ catalogued as note.&lt;slug&gt;, referenced from the item's refs. The refusal says
/// so, because a caller told only "too long" has nowhere to go. Same idiom as
/// <see cref="ResultBudget"/>: <see cref="EnvironmentVariable"/> overrides without a rebuild,
/// and anything that is not a positive integer falls back to the default rather than to
/// unbounded or to zero.
///
/// 'next' has its own FIXED ceiling and no override. It is the resume cursor -- the one thing
/// to do first on return -- and a cursor that needs a thousand characters is notes wearing the
/// wrong label. The live list held one of 528 characters when this was written.
///
/// A REFUSAL, NEVER A CUT. Truncating notes silently is how the last line of a log -- the one
/// that says what to do next -- goes missing.
/// </remarks>
public static class NotesBudget
{
    /// <summary>Characters an item's notes may hold after a write, absent an override.</summary>
    public const int Default = 8_000;

    /// <summary>Characters the resume cursor may hold. Fixed: there is no override.</summary>
    public const int NextCeiling = 1_000;

    /// <summary>
    /// Environment variable holding an integer character ceiling that replaces <see cref="Default"/>.
    /// </summary>
    public const string EnvironmentVariable = "JANET_NOTES_BUDGET";

    /// <summary>
    /// Environment variable that turns automatic archiving off for a whole process.
    /// </summary>
    /// <remarks>
    /// The process-level half of the opt-out, resolved per call exactly as
    /// <see cref="EnvironmentVariable"/> is, and matching JANET_RESULT_BUDGET and
    /// JANET_GRAPH_GUARD. The per-call half is the 'split' argument. Default is ON, and it is
    /// the right default BECAUSE the fallback is a refusal rather than a corruption: the worst
    /// an unwanted archive does is move old prose to a named file and say so twice, in the
    /// response and in the notes.
    /// </remarks>
    public const string AutosplitVariable = "JANET_NOTES_AUTOSPLIT";

    /// <summary>Whether automatic archiving is on for this process.</summary>
    public static bool Autosplit => ResolveAutosplit(Environment.GetEnvironmentVariable(AutosplitVariable));

    /// <summary>
    /// Reads the autosplit switch. Only an explicit off spelling turns it off.
    /// </summary>
    /// <remarks>
    /// Missing, blank and anything unrecognised all mean ON, so a typo leaves the behaviour the
    /// default rather than silently disabling a feature whose absence looks like a refusal.
    /// </remarks>
    public static bool ResolveAutosplit(string? raw) =>
        !OffSpellings.Contains(raw?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] OffSpellings = ["off", "0", "false", "no"];

    /// <summary>
    /// How much an archive leaves behind: HALF the ceiling, never the ceiling.
    /// </summary>
    /// <remarks>
    /// Cutting back to 7,999 characters means the next append archives again, and then again --
    /// ten files, which is the failure the ceiling exists to avoid restated one level up.
    /// Halving gives roughly a ceiling's worth of headroom back, so an item that grows a
    /// paragraph at a time archives on the order of once a month rather than once an append.
    /// It moves with the override: JANET_NOTES_BUDGET=20000 retains 10,000.
    /// </remarks>
    public static int Retention(int ceiling) => ceiling / 2;

    /// <summary>The retention target in force, for the ceiling in force.</summary>
    public static int CurrentRetention => Retention(Current);

    /// <summary>Where long-form belongs instead, stated in every refusal.</summary>
    public const string Escape =
        "Long-form belongs in a catalogued note: write it to notes\\<slug>.md in the repo, " +
        "`janet research add` it as note.<slug>, put the id in refs, and replace notes with a " +
        "shorter working log.";

    /// <summary>The ceiling in force: the override if it is a positive integer, else the default.</summary>
    public static int Current => Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Reads a ceiling from the raw text of the override. Missing, blank, non-numeric, zero and
    /// negative all mean <see cref="Default"/>: there is no way to spell "unbounded".
    /// </summary>
    public static int Resolve(string? raw) =>
        int.TryParse(raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : Default;

    /// <summary>The message a notes write over the ceiling is refused with.</summary>
    /// <param name="topic">The item being written.</param>
    /// <param name="length">The length the notes WOULD have had after the write.</param>
    /// <param name="ceiling">The ceiling in force when it was measured.</param>
    /// <param name="because">
    /// One sentence saying why an automatic archive did not save this write, appended verbatim.
    /// Null on the paths where archiving was never in play -- an add, or a replacement.
    /// </param>
    public static string NotesRefusal(string topic, int length, int ceiling, string? because = null) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Refused: notes for '{0}' would be {1:N0} characters, and the ceiling is {2:N0} " +
            "({3} overrides). Nothing was written. {4}",
            topic,
            length,
            ceiling,
            EnvironmentVariable,
            Escape) + (because is null ? string.Empty : " " + because);

    /// <summary>Why an over-ceiling append could not be archived: there is nowhere to cut.</summary>
    /// <remarks>
    /// The 9,000-character single paragraph, which behaves exactly as it did before archiving
    /// existed. A paragraph is one indivisible unit of prose and the tool has no business
    /// guessing where a thought ends.
    /// </remarks>
    public static string NoBoundaryRefusal(int length) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "No paragraph or line boundary falls in the archivable range -- the stored notes are " +
            "one block of {0:N0} characters, so nothing can be moved without cutting prose. " +
            "Split it by hand, or write it to a note.",
            length);

    /// <summary>Why an over-ceiling append could not be archived: nothing may be cut out of what stays.</summary>
    /// <remarks>
    /// There are boundaries, but the smallest tail any of them leaves is still over the
    /// retention target, because the newest stored block plus the text the caller sent in this
    /// call is larger than it -- and text sent in this call is never cut, since splitting it
    /// would put one authored passage in two places and the malformed-markup guard scans that
    /// fragment whole.
    /// </remarks>
    public static string NoFitRefusal(int smallest, int retention, int fragment) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Archiving cannot get these notes under the {0:N0}-character retention target: the " +
            "smallest that can be kept is {1:N0} characters, because the newest stored block " +
            "plus the {2:N0} characters you sent in this call stay together and text sent in " +
            "this call is never cut. Send it a paragraph at a time, or replace the notes with a " +
            "shorter working log.",
            retention,
            smallest,
            fragment);

    /// <summary>Why an over-ceiling append was not archived: the caller turned archiving off.</summary>
    public static string AutosplitOffRefusal() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Automatic archiving of the oldest notes is off for this call (split=false, " +
            "--no-split, or {0}=off), so the append was refused whole as it would have been " +
            "before archiving existed.",
            AutosplitVariable);

    /// <summary>Why an over-ceiling append was not archived: somebody else wrote first.</summary>
    /// <remarks>
    /// The archive file is prepared before the write is queued, so a writer that changed this
    /// item in between leaves the plan describing text that is no longer there. Applying it
    /// would archive prose the plan never read; refusing costs one orphan file and a retry.
    /// </remarks>
    public static string StaleRefusal(string where) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "An archive to {0} was prepared for this append, but another writer changed the " +
            "item first, so it describes text that is no longer there and was not applied. " +
            "Nothing was written. Retry: the next attempt archives what is there now.",
            where);

    /// <summary>The message a next write over its ceiling is refused with.</summary>
    public static string NextRefusal(string topic, int length) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Refused: next for '{0}' would be {1:N0} characters, and the ceiling is {2:N0}. " +
            "Nothing was written. 'next' is the resume cursor -- the one thing to do first on " +
            "return -- so keep it to a sentence and put the detail in notes. {3}",
            topic,
            length,
            NextCeiling,
            Escape);
}
