using System.Globalization;

namespace Janet.Core;

/// <summary>
/// The size a tool result may reach before it is refused, and the refusal that says what to
/// call instead. Shared by every front end so they cannot disagree about the number.
/// </summary>
/// <remarks>
/// EVERY OTHER CAP IN THIS CODEBASE BOUNDS A COUNT, and none of them bounds a size. 'first'
/// limits ranked results after scoring and is switched off for selector requests, because a
/// selector is a request for a known set; the thread list has never been capped at all. So a
/// result could grow past what any client will carry, and did: on 2026-09-03 thread_show
/// returned 229KB (356KB with all=true), research_query with all=true 214KB while reporting
/// truncated:false, api_doc_query 581KB, and dotnet_check emits every build error and every
/// test failure. Lori: "there should not be a way to get to this."
///
/// THE NUMBER IS CHOSEN, NOT NEGOTIATED. The MCP handshake carries no result limit -- no size,
/// no token count -- so the server cannot learn what the client will accept. Claude Code's
/// default ceiling is 25,000 tokens (MAX_MCP_OUTPUT_TOKENS) at roughly four characters per
/// token; a measured 26,613-character thread_report fits comfortably and a 174,129-character
/// thread_show did not. 100,000 characters sits under that ceiling with room for the client's
/// own framing. Another client with a different ceiling sets <see cref="EnvironmentVariable"/>
/// rather than rebuilding; a value that is not a positive integer is ignored, so a typo in an
/// environment falls back to the default instead of to unbounded or to zero.
///
/// A REFUSAL, NEVER A CUT. A JSON document truncated at a character count parses as something
/// wrong or does not parse at all, and either is worse than the honest statement "this was too
/// large -- ask a narrower question". The refusal names the tool, the actual size, the budget,
/// and the tool's own narrowing calls, because a caller told only "too big" has nowhere to go.
/// </remarks>
public static class ResultBudget
{
    /// <summary>Characters a tool result may hold before it is refused, absent an override.</summary>
    public const int Default = 100_000;

    /// <summary>
    /// Environment variable holding an integer character budget that replaces <see cref="Default"/>.
    /// </summary>
    public const string EnvironmentVariable = "JANET_RESULT_BUDGET";

    /// <summary>The budget in force: the override if it is a positive integer, else the default.</summary>
    public static int Current => Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Reads a budget from the raw text of the override. Missing, blank, non-numeric, zero and
    /// negative all mean <see cref="Default"/>: there is no way to spell "unbounded".
    /// </summary>
    public static int Resolve(string? raw) =>
        int.TryParse(raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : Default;

    /// <summary>
    /// Null when the result fits the budget in force; otherwise the message to refuse it with.
    /// </summary>
    /// <param name="toolName">The tool whose result this is, as the caller knows it.</param>
    /// <param name="resultText">The text the tool produced, in full.</param>
    /// <param name="narrowing">
    /// What to call instead, in the tool's own terms. The front end supplies this because only
    /// it knows the tool names; Core knows the number.
    /// </param>
    public static string? Refusal(string toolName, string resultText, string narrowing) =>
        Refusal(toolName, resultText, narrowing, Current);

    /// <summary>Same as <see cref="Refusal(string, string, string)"/> against an explicit budget.</summary>
    public static string? Refusal(string toolName, string resultText, string narrowing, int budget)
    {
        int size = resultText.Length;
        if (size <= budget)
        {
            return null;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} returned {1:N0} characters; the result budget is {2:N0} ({3}). The result was " +
            "refused rather than cut, because a truncated JSON document parses as something " +
            "wrong. Narrow the call: {4} The `janet` CLI twin has no result limit -- redirect " +
            "its output to a file and read the part you need.",
            toolName,
            size,
            budget,
            EnvironmentVariable,
            narrowing);
    }
}
