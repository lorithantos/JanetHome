using System.ComponentModel;
using Janet.Core;
using ModelContextProtocol.Server;

namespace Janet.Mcp;

/// <summary>
/// Azure access tokens, borrowed from the Azure CLI's sign-in.
/// </summary>
/// <remarks>
/// This is the surface the cache was built for. The server process outlives any one session, so
/// a token fetched to answer one question is still good for the next few hundred -- and the
/// alternative, an `az account get-access-token` per call, is one to two seconds of Python
/// startup each time.
///
/// No scope allow-list beyond the alias table, deliberately. The token is issued to the user who
/// ran `az login` and carries exactly the access they already have; refusing a scope here would
/// not remove any permission, it would only mean the caller shells out to `az` instead and gets
/// the same token with none of the caching.
/// </remarks>
[McpServerToolType]
public static class AzTools
{
    [McpServerTool(Name = "az_token")]
    [Description(
        "An Azure AD access token for calling Azure REST APIs directly, borrowed from the " +
        "machine's `az login`. Cached in this server process and reused until near expiry, so " +
        "asking repeatedly is cheap -- an uncached acquisition shells out to the Azure CLI and " +
        "costs a second or two. BY DEFAULT THIS RETURNS METADATA, NOT THE TOKEN: scope, tenant, " +
        "expiry and whether it was cached. That answers 'is a token available and does it last " +
        "long enough' without writing a live credential into the transcript, which is persisted " +
        "and re-sent every turn. Pass raw=true when you actually need the value to make a call. " +
        "Scope takes an alias (arm, management, storage, graph, keyvault, sql, servicebus, " +
        "cosmos) or a full scope URI; an unknown alias is refused with the list rather than " +
        "guessed at.")]
    public static string Token(
        [Description("Resource alias (arm, storage, graph, keyvault, sql, servicebus, cosmos) or a full scope URI. Defaults to arm.")]
        string? scope = null,
        [Description("Tenant id to authenticate against. Omit to use whatever `az` is currently signed in to.")]
        string? tenant = null,
        [Description("Include the token itself. Leave false unless you are about to use it: the value lands in the transcript, which is written to disk and re-sent on every later turn.")]
        bool raw = false,
        [Description("Acquire a fresh token even if a live one is cached. Use after `az login` to a different account, not as a retry.")]
        bool refresh = false) =>
        AzTokenJson.Serialize(AzToken.Acquire(new AzTokenRequest
        {
            Scope = scope ?? AzToken.DefaultResource,
            Tenant = tenant,
            Raw = raw,
            Refresh = refresh,
        }));
}
