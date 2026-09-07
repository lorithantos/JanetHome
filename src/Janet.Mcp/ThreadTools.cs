using System.ComponentModel;
using Janet.Core;
using ModelContextProtocol.Server;

namespace Janet.Mcp;

/// <summary>
/// The thread-item list: what you were doing, and what to do first on return.
/// </summary>
/// <remarks>
/// No path parameter on any tool. The list lives at one well-known location per machine, and a
/// caller free to point these at an arbitrary file could silently keep two lists -- which is
/// the failure the list exists to prevent, since the whole value is that there is one place the
/// unwind path is written down. The CLI keeps --path because tests and hooks need it.
/// </remarks>
[McpServerToolType]
public static class ThreadTools
{
    [McpServerTool(Name = "thread_show")]
    [Description(
        "The investigation topics from this session and the last, with which one is in focus. " +
        "Read this when resuming work, or when you have lost the thread of what you were doing. " +
        "Each item's 'notes' is a LEAD by default -- the first non-empty line, capped at 200 " +
        "characters, the same lead thread_report carries -- with 'notesLength' (the stored " +
        "size) beside it and 'notesTruncated': true whenever the lead is not the whole text. " +
        "To read one item's notes whole, pass topic AND full=true; full is refused with area " +
        "or with no selector, because notes are returned one item at a time. Pass area to see " +
        "only one project's items -- the list is shared by every repo on this machine, so an " +
        "unnarrowed read is mostly someone else's work. Completed items are excluded unless " +
        "all=true -- nothing is ever deleted. " +
        "Focus is one cursor PER AREA, so 'active' names the focus of the area you narrowed to " +
        "and is null when you passed no area -- it cannot name four cursors at once. Use " +
        "thread_report to see every area's cursor in one answer. A topic that " +
        "matches nothing or several items, and an area nothing is filed under, are refused with " +
        "a message saying what to do -- they are not answered with an empty list. The 'error' " +
        "field reports a list that could not be read; it is a fact about the list, not a failure " +
        "of this call, so check it rather than assuming an empty result means no work is open. " +
        "A result over the result budget (100,000 characters by default) is REFUSED, never cut, " +
        "with a message naming the size and the narrowing to make; an unnarrowed read of this " +
        "machine's list is over it.")]
    public static string Show(
        [Description("Include completed items as well as open ones.")] bool all = false,
        [Description(
            "Substring of ONE item's topic, case-insensitive. Returns that item; add full=true " +
            "for its notes whole. Ambiguous is refused with every candidate named, never " +
            "resolved to the first match. A '*' is a literal asterisk, not a wildcard.")]
        string? topic = null,
        [Description(
            "Area to narrow to, case-insensitive substring. '(unfiled)' is the group of items " +
            "with no area set -- items are never guessed into a neighbouring area.")]
        string? area = null,
        [Description(
            "Return the selected item's notes whole instead of the lead. Allowed ONLY with " +
            "topic; refused with area or with no selector, since expanding notes across a set " +
            "is the oversized read the lead exists to prevent.")]
        bool full = false) =>
        ThreadJson.Serialize(ThreadItems.Show(null, all, topic, area, full));

    [McpServerTool(Name = "thread_report")]
    [Description(
        "START HERE when resuming: the topics, which one is in focus, and how large each item's " +
        "notes are -- WITHOUT the note bodies. Answers 'where was I' for a fraction of the cost; " +
        "thread_show on the same list returns every note in full and is refused over the result " +
        "budget unless narrowed. Each item carries notesLead (the first non-empty line) and notesLength " +
        "(the full size in characters), and the envelope totals what was withheld, so nothing is " +
        "omitted silently. Read one item in full with thread_show once you know which one you " +
        "want. Each item carries the area it is filed under; pass area to see one project's " +
        "items only, which is usually what you want, since this list is shared by every repo on " +
        "this machine. Focus is one cursor PER AREA: 'active' names the focus of the area you " +
        "narrowed to, and is null when you passed no area, because a single field cannot carry " +
        "every project's cursor. The envelope's 'areas' is the map of the WHOLE open list -- one " +
        "{area, open, active} row per area in use, sorted by name -- and it ignores the " +
        "narrowing, so a report of one project still says how much is open elsewhere, under " +
        "which labels, and what each of them has in hand. Nothing in focus anywhere is every " +
        "row's 'active' being null. " +
        "Completed items are excluded unless all=true, and never counted in 'areas'. " +
        "lead=false omits each item's notesLead key (notesLength stays), for when the map is " +
        "all you need.")]
    public static string Report(
        [Description("Include completed items as well as open ones.")] bool all = false,
        [Description(
            "Substring of ONE item's topic, case-insensitive. Ambiguous or unmatched is refused " +
            "rather than answered with an empty list.")]
        string? topic = null,
        [Description(
            "Area to narrow to, case-insensitive substring. '(unfiled)' is the group of items " +
            "with no area set.")]
        string? area = null,
        [Description(
            "Carry each item's notesLead (first non-empty line of its notes, capped at 200). " +
            "false omits the key; notesLength is carried either way.")]
        bool lead = true) =>
        ThreadJson.Serialize(ThreadItems.Report(null, all, topic, area, lead));

    [McpServerTool(Name = "thread_add")]
    [Description(
        "Record an investigation topic. Use this the moment you notice work you are not doing " +
        "now -- that is the point: debugging is depth-first and working memory is not, so the " +
        "descent gets written down. Adding does NOT take focus unless active=true, so noting " +
        "something costs you nothing. Refuses a topic that already exists rather than creating " +
        "a second one that selectors cannot tell apart. Set area to the project this belongs " +
        "to: the list is shared by every repo on this machine, and an unfiled item is only " +
        "findable by reading all of them. Notes are CAPPED at 8,000 characters " +
        "(JANET_NOTES_BUDGET overrides) and next at 1,000; a write over either is refused " +
        "whole, with the escape named: put long-form in a catalogued note under notes\\ and " +
        "reference it from refs.")]
    public static string Add(
        [Description("What the investigation is about. One line, distinctive enough to select on.")]
        string topic,
        [Description("Detail too small or too fresh to be worth a research.json node.")]
        string? notes = null,
        [Description("The resume cursor: the one thing to do first on return.")]
        string? next = null,
        [Description("Catalog node ids that carry the context for this topic.")]
        string[]? refs = null,
        [Description(
            "Also take focus, parking whatever held it IN THIS ITEM'S OWN AREA. Focus is one " +
            "cursor per area, so no other project's session is displaced.")]
        bool active = false,
        [Description(
            "Which project or area this belongs to, free text -- a stored label, never derived " +
            "from the topic. Omit and the item is '(unfiled)', which is a real group, not a " +
            "guess at which project it resembles.")]
        string? area = null) =>
        ThreadJson.Serialize(ThreadItems.Add(
            null, topic, notes ?? string.Empty, next ?? string.Empty, refs ?? [], active, area));

    [McpServerTool(Name = "thread_update")]
    [Description(
        "Amend an item in place. Omitted fields are left alone; an empty string clears one, " +
        "which is how you drop a resume cursor that no longer applies. Select by topic " +
        "(case-insensitive substring); with none, this acts on the item in focus, which works " +
        "while a single area holds focus and is otherwise refused with every cursor named. " +
        "An ambiguous topic is refused and both candidates named, rather than resolved to a " +
        "first match -- amending the wrong item is how notes get lost. Notes are CAPPED at " +
        "8,000 characters (JANET_NOTES_BUDGET overrides) and next at 1,000, measured on what " +
        "the item would hold AFTER the write -- so an appendNotes that crosses the ceiling is " +
        "refused whole, not trimmed. The escape: write the long-form to notes\\<slug>.md, " +
        "`janet research add` it as note.<slug>, put the id in refs, and REPLACE notes with a " +
        "shorter working log. Writes that leave notes and next alone (status, refs, area) " +
        "still succeed on an item already over the ceiling.")]
    public static string Update(
        [Description("Substring of the topic to act on. Omit to act on the item in focus.")]
        string? topic = null,
        [Description(RetiredIndex)] int? index = null,
        [Description(
            "Replacement notes, or empty to clear. Omit to leave alone. The result -- replaced " +
            "or appended -- must fit the 8,000-character ceiling.")]
        string? notes = null,
        [Description("Replacement resume cursor, or empty to clear. Omit to leave alone.")]
        string? next = null,
        [Description("Replacement catalog node ids. Omit to leave alone.")]
        string[]? refs = null,
        [Description(
            "active, parked, or done. Setting active parks whatever held focus in THIS ITEM'S " +
            "OWN AREA, and no other area's.")]
        string? status = null,
        [Description("Add to the existing notes rather than replacing them.")]
        bool appendNotes = false,
        [Description("Add to the existing refs rather than replacing them. Repeats are kept.")]
        bool appendRefs = false,
        [Description(
            "Which project or area this item belongs to, or empty to unfile it. Omit to leave " +
            "alone. This is how an existing item gets labelled -- none were backfilled, so most " +
            "read as '(unfiled)' until someone says otherwise.")]
        string? area = null) =>
        ThreadJson.Serialize(ThreadItems.Update(
            null, Selector(topic, index), notes, next, refs, status, appendNotes, appendRefs,
            area));

    [McpServerTool(Name = "thread_complete")]
    [Description(
        "Mark an item finished. This is a status change, not a deletion -- the item stays in the " +
        "list and stops showing by default, so finishing work leaves a record of having done it. " +
        "Clears focus for THAT ITEM'S AREA only. With no selector it completes the item in " +
        "focus, which works while a single area holds focus and is otherwise refused with every " +
        "area and topic named -- pass a topic to say which one you meant.")]
    public static string Complete(
        [Description("Substring of the topic to complete. Omit to complete the item in focus.")]
        string? topic = null,
        [Description(RetiredIndex)] int? index = null) =>
        ThreadJson.Serialize(ThreadItems.Complete(null, Selector(topic, index)));

    [McpServerTool(Name = "thread_set_active")]
    [Description(
        "Move focus to an item, or clear it with none=true. Focus is one cursor PER AREA: " +
        "taking it parks whatever held focus in THAT ITEM'S OWN AREA and nothing else, so " +
        "picking up JanetHome work leaves another repo's session holding what it was holding. " +
        "The parked topic is reported as 'previous' so the switch is visible. With none=true, " +
        "pass area to say whose cursor to clear; without it the clear succeeds only while a " +
        "single area holds focus, and is otherwise refused with every area and topic named. " +
        "A completed item cannot take focus until it is reopened by setting its status to parked.")]
    public static string SetActive(
        [Description("Substring of the topic to focus on.")] string? topic = null,
        [Description(RetiredIndex)] int? index = null,
        [Description("Clear focus entirely rather than moving it.")] bool none = false,
        [Description(
            "Which area's focus to clear, case-insensitive substring resolving to exactly one " +
            "area. Applies only with none=true -- an item takes focus in the area it is filed " +
            "under. '(unfiled)' is an area like any other and has its own cursor.")]
        string? area = null) =>
        ThreadJson.Serialize(ThreadItems.SetActive(null, none ? null : Selector(topic, index), area));

    private const string RetiredIndex =
        "RETIRED -- do not pass. Supplying it is an error. Select by topic instead.";

    /// <summary>Builds a topic selector, refusing a retired positional one.</summary>
    /// <remarks>
    /// The parameter outlives the feature on purpose. Deleting it would leave an unknown
    /// property that the argument binder drops in silence, so a caller still passing index
    /// would act on whatever happened to be in focus -- the wrong-item write this removal
    /// exists to prevent. Kept, so the mistake is a refusal instead.
    /// </remarks>
    private static ThreadSelector Selector(string? topic, int? index) =>
        index is not null
            ? throw new GraphException(
                "index was removed: the list is keyed by topic, and a displayed position is " +
                "wrong by the number of completed items above it. Select with topic.")
            : new ThreadSelector { Topic = topic ?? string.Empty };
}
