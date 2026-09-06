using System.Globalization;

namespace Janet.Core;

/// <summary>
/// Refuses a field whose text is the body of a mangled tool call rather than content. Shared by
/// the thread-item and research-node write paths, so the three front ends inherit one guard
/// and cannot word its refusal differently.
/// </summary>
/// <remarks>
/// When an agent's tool-call markup is mangled, the harness hands the server ONE oversized
/// string holding the rest of the call -- the closing tag of the field it was in, the next
/// parameter's opening tag, that parameter's content, the closing invoke tag -- and until
/// 2026-09-05 both write paths stored it verbatim and reported success. Two research nodes
/// (2026-09-03) and at least eight thread items (2026-09-05) were corrupted that way, each with
/// its resume cursor swallowed into notes and next left empty. The refusal turns the silent
/// write into something the agent can see and re-issue.
///
/// THE SIGNATURES ARE DELIBERATELY FEW: a closing tag for the two field names and the two
/// call-markup names, and the literal attribute text that opens a parameter (which also covers
/// its opening tag). No general angle-bracket rejection -- prose summaries legitimately say
/// "A -> B" and name List&lt;string&gt;, and a guard that fired on those would be worked around
/// rather than obeyed.
///
/// The scan runs on what the caller SENT, never on what the item would hold afterwards. Items
/// that describe this very bug with the literal tags in their notes exist and must stay
/// amendable, so an append scans its fragment alone; the callers say so where they call.
/// </remarks>
public static class MalformedInput
{
    /// <summary>What a mangled call looks like from inside one field. Matched case-insensitively.</summary>
    public static readonly IReadOnlyList<string> Signatures =
        ["</notes>", "</next>", "</invoke>", "</parameter>", "parameter name="];

    /// <summary>
    /// The earliest signature in the text and where it starts, or null when the text is clean
    /// or absent.
    /// </summary>
    public static (string Signature, int Offset)? Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        (string Signature, int Offset)? first = null;

        foreach (string signature in Signatures)
        {
            int offset = text.IndexOf(signature, StringComparison.OrdinalIgnoreCase);

            if (offset >= 0 && (first is null || offset < first.Value.Offset))
            {
                first = (signature, offset);
            }
        }

        return first;
    }

    /// <summary>The message a write carrying a signature is refused with.</summary>
    /// <param name="field">The field the text was bound for: notes, summary, caveats[1], ...</param>
    /// <param name="subject">The thread item's topic or the research node's id.</param>
    public static string Refusal(string field, string subject, string signature, int offset) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Refused: {0} for '{1}' contains '{2}' at character {3:N0}. Nothing was written. " +
            "This is the body of a tool call, not content -- re-issue the call with one " +
            "parameter per field.",
            field,
            subject,
            signature,
            offset);

    /// <summary>Throws the refusal when the text carries a signature. Null and empty pass.</summary>
    public static void Ensure(string field, string subject, string? text)
    {
        if (Find(text) is (string signature, int offset))
        {
            throw new GraphException(Refusal(field, subject, signature, offset));
        }
    }
}
