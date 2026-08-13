using Janet.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Janet.Mcp;

/// <summary>
/// Makes a tool's own error message reach the caller.
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
/// </remarks>
internal static class Surfaced
{
    /// <summary>
    /// Registered once on the server rather than wrapped around each tool, so a tool added
    /// later cannot forget it.
    /// </summary>
    public static void Filter(IMcpRequestFilterBuilder filters) =>
        filters.AddCallToolFilter(next => async (context, cancellation) =>
        {
            try
            {
                return await next(context, cancellation);
            }
            catch (GraphException ex)
            {
                throw new McpException(ex.Message);
            }
        });
}
