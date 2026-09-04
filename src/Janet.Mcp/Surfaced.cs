using Janet.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Janet.Mcp;

/// <summary>
/// Makes a tool's own error message reach the caller, and refuses a result too large to carry.
/// </summary>
/// <remarks>
/// MEASURED 2026-08-12, and it applied to every tool here: an exception thrown out of a tool
/// body reaches the client as "An error occurred invoking 'research_add'." and nothing else.
/// The SDK hides exception detail by default, sensibly, since an arbitrary exception can carry
/// internals. The cost was that none of the messages this codebase is careful about arrived --
/// not "id already exists", not the list of candidates an ambiguous topic prints instead of
/// guessing, not "point the search root at a build output", not "that handle is gone, start the
/// check again". A tool that fails without saying why is the failure the whole repo is written
/// against, and it was shipping in the one place nobody could see it from.
///
/// GraphException is Janet's own signal for "the caller asked for something that cannot be
/// done, and here is what to do instead", so it is exactly what should be surfaced.
/// Everything else stays hidden and generic, deliberately: an unexpected exception is a bug
/// report, not a message to a caller.
///
/// THE SIZE ARM, 2026-09-03. A result over <see cref="ResultBudget"/> is refused with the same
/// mechanism -- an McpException carrying the tool, the size, the budget and that tool's
/// narrowing calls from <see cref="Narrowing"/> -- and is NEVER cut down to fit. The argument
/// is the one above, read the other way: a truncated JSON document is a message that lies
/// about what it is, and a client parsing it gets something wrong rather than something
/// partial. The client's own ceiling is invisible to this server (the handshake carries no
/// size or token field), so the budget is a chosen number, and every cap that existed before
/// this bounded a count after ranking and was disabled for selector requests -- thread_show
/// reached 229KB, research_query with all=true 214KB while reporting truncated:false.
/// </remarks>
internal static class Surfaced
{
    /// <summary>
    /// Registered once on the server rather than wrapped around each tool, so a tool added
    /// later cannot forget it. Both transports register it.
    /// </summary>
    public static void Filter(IMcpRequestFilterBuilder filters) =>
        filters.AddCallToolFilter(next => async (context, cancellation) =>
        {
            CallToolResult result;
            try
            {
                result = await next(context, cancellation);
            }
            catch (GraphException ex)
            {
                throw new McpException(ex.Message);
            }

            string? refusal = Refusal(context.Params?.Name, result);
            return refusal is null ? result : throw new McpException(refusal);
        });

    /// <summary>
    /// The refusal for a result over budget, or null when it fits. Factored out of the filter
    /// so it can be tested without an <see cref="IMcpRequestFilterBuilder"/>, which takes a
    /// server to construct; the filter body above is only the plumbing around this.
    /// </summary>
    /// <remarks>
    /// Measures the TEXT blocks, because that is what every tool here returns -- a string the
    /// SDK wraps in one TextContentBlock -- and what a client renders into its result budget.
    /// A missing tool name is reported as such rather than defaulted to some tool's hint.
    /// </remarks>
    internal static string? Refusal(string? toolName, CallToolResult result)
    {
        string name = string.IsNullOrEmpty(toolName) ? "(unnamed tool)" : toolName;
        string text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return ResultBudget.Refusal(name, text, Narrowing.For(name));
    }
}
