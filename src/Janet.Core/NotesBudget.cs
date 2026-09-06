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
    public static string NotesRefusal(string topic, int length, int ceiling) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Refused: notes for '{0}' would be {1:N0} characters, and the ceiling is {2:N0} " +
            "({3} overrides). Nothing was written. {4}",
            topic,
            length,
            ceiling,
            EnvironmentVariable,
            Escape);

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
