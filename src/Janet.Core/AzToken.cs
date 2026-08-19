using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;

namespace Janet.Core;

/// <summary>What the caller wants a token for.</summary>
public sealed class AzTokenRequest
{
    /// <summary>An alias from <see cref="AzToken.Resources"/>, or a literal scope.</summary>
    public string Scope { get; init; } = AzToken.DefaultResource;

    /// <summary>Tenant to authenticate against. Null means whatever `az` is currently signed in to.</summary>
    public string? Tenant { get; init; }

    /// <summary>Include the token itself. Off by default -- see the remarks on <see cref="AzTokenResult.Token"/>.</summary>
    public bool Raw { get; init; }

    /// <summary>Acquire a new token even if a live one is cached.</summary>
    public bool Refresh { get; init; }
}

/// <summary>A token, or -- by default -- everything about one except its value.</summary>
/// <remarks>
/// A record rather than a class so a front end can hand the value to one sink and print the
/// metadata to another -- `result with { Token = null }` is how --out-file writes the token to
/// disk without also echoing it to a terminal.
/// </remarks>
public sealed record AzTokenResult
{
    /// <summary>Format version. See contracts\az-token.schema.json.</summary>
    /// <remarks>2 added servedBy, so a CLI answer says whether it borrowed a server's cache.</remarks>
    public int Contract => 2;

    /// <summary>What the caller asked for, verbatim: an alias, or a scope they typed out.</summary>
    public required string Requested { get; init; }

    /// <summary>The scope actually requested from Azure, after alias resolution.</summary>
    public required string Scope { get; init; }

    /// <summary>Alias this resolved through, or null when the caller passed a literal scope.</summary>
    public string? Resource { get; init; }

    public required DateTimeOffset ExpiresOn { get; init; }

    /// <summary>
    /// Seconds until expiry, floored at zero.
    /// </summary>
    /// <remarks>
    /// Present so a caller can decide whether the token outlives the work it is for without
    /// parsing a timestamp and knowing what "now" the server meant. Floored rather than allowed
    /// negative: a negative lifetime is not a thing a caller should have to interpret, and this
    /// value is never produced for an expired token anyway.
    /// </remarks>
    public required int ExpiresInSeconds { get; init; }

    /// <summary>True when this came from the process cache without going to `az`.</summary>
    public required bool Cached { get; init; }

    /// <summary>
    /// Whether the process you asked worked this out, or borrowed it from a running janet-mcp.
    /// </summary>
    /// <remarks>
    /// Only the CLI can ever report Server: a tool call is already talking to the server, so
    /// from there the answer is always local. The field exists because without it cached:false
    /// from the CLI is ambiguous between "there was no server to ask" and "the server's cache
    /// was cold" -- the not-applicable-versus-missing confusion this codebase keeps meeting,
    /// and the two want completely different reactions.
    /// </remarks>
    public ServedBy ServedBy { get; init; } = ServedBy.Process;

    /// <summary>Tenant the token was issued for, when one was requested.</summary>
    public string? Tenant { get; init; }

    /// <summary>Almost always "Bearer". Reported rather than assumed.</summary>
    public required string TokenType { get; init; }

    /// <summary>
    /// The token itself, and null unless it was explicitly asked for.
    /// </summary>
    /// <remarks>
    /// DEFAULTS OFF ON PURPOSE. An MCP tool result is written into the conversation transcript,
    /// which is persisted to disk and re-sent on every subsequent turn -- so returning a bearer
    /// credential by default would write a live secret into a file that outlives it, once per
    /// call, forever. Metadata answers most of the questions anyone actually asks of a token
    /// ("is one available", "does it last long enough", "which tenant"), and the value itself is
    /// available to whoever states that they want it.
    /// </remarks>
    public string? Token { get; init; }
}

/// <summary>
/// Azure access tokens, acquired through the Azure CLI and cached for the life of the process.
/// </summary>
/// <remarks>
/// WHY THE CLI AND NOT A SERVICE PRINCIPAL: there is no service principal. The machine signs in
/// with `az login` as a user, and every Azure operation in this repo already runs through `az`
/// (see Invoke-BuildDeploy.ps1). Borrowing that same sign-in means no secret to store, no app
/// registration to keep in step with the roles it needs, and one identity to reason about
/// instead of two.
///
/// WHY A CACHE AT ALL: `az account get-access-token` is a Python process. It keeps its own
/// on-disk MSAL cache, so it rarely does real network work -- but it pays process startup every
/// single time, which is one to two seconds. AzureCliCredential does not cache across calls, so
/// without this dictionary a caller in a loop pays that per iteration. In the janet-mcp server
/// the process outlives any one session, so a token acquired for one question is still good for
/// the next few hundred: this is the ordinary server-side arrangement, one credential held for
/// the process lifetime with tokens reused until they are nearly expired.
///
/// The cache is keyed on scope AND tenant because a token is only valid for the pair. Keying on
/// scope alone would hand a token for the wrong directory to whoever asked second, which fails
/// as a 401 far from here.
/// </remarks>
public sealed class AzTokenCache
{
    private readonly ConcurrentDictionary<string, AccessToken> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TokenCredential> _credentials = new(StringComparer.Ordinal);
    private readonly Func<string?, TokenCredential> _factory;
    private readonly TimeProvider _clock;

    /// <summary>
    /// How close to expiry a cached token is still handed out.
    /// </summary>
    /// <remarks>
    /// A token that expires in twenty seconds is worthless to a caller about to start a
    /// multi-minute upload, and the caller has no way to know that from the value alone. Five
    /// minutes is the conventional refresh skew and is what Azure's own SDK pipelines use.
    /// Applies only when the token carries no RefreshOn of its own.
    /// </remarks>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long `az` gets to answer before the acquisition is abandoned.
    /// </summary>
    /// <remarks>
    /// Declared rather than inherited. `az` is an external tool that can block on an interactive
    /// re-auth prompt it has no terminal to show, and an agent blocked on a hanging call produces
    /// no output and no diagnosis -- the failure mode DESIGN-NOTES section 8 exists to prevent.
    /// </remarks>
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    public AzTokenCache(Func<string?, TokenCredential>? credentialFactory = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _factory = credentialFactory ?? DefaultCredential;
    }

    private static TokenCredential DefaultCredential(string? tenant) =>
        new AzureCliCredential(new AzureCliCredentialOptions
        {
            TenantId = tenant,
            ProcessTimeout = ProcessTimeout,
        });

    public AzTokenResult Acquire(AzTokenRequest request)
    {
        string requested = string.IsNullOrWhiteSpace(request.Scope) ? AzToken.DefaultResource : request.Scope.Trim();
        (string scope, string? resource) = AzToken.ResolveScope(requested);

        string? tenant = string.IsNullOrWhiteSpace(request.Tenant) ? null : request.Tenant.Trim();
        string key = $"{scope}|{tenant ?? string.Empty}";

        DateTimeOffset now = _clock.GetUtcNow();
        bool cached = false;
        AccessToken token;

        if (!request.Refresh && _tokens.TryGetValue(key, out AccessToken held) && IsUsable(held, now))
        {
            token = held;
            cached = true;
        }
        else
        {
            token = Fetch(scope, tenant);
            _tokens[key] = token;
        }

        return new AzTokenResult
        {
            Requested = requested,
            Scope = scope,
            Resource = resource,
            ExpiresOn = token.ExpiresOn,

            // Clamped at zero rather than allowed negative. A freshly fetched token that is
            // already expired is a real possibility -- a machine whose clock is wrong -- and it
            // should read as "no time left", not as a negative duration a caller has to sanity
            // check before using.
            ExpiresInSeconds = (int)Math.Max(0, (token.ExpiresOn - now).TotalSeconds),
            Cached = cached,
            Tenant = tenant,
            TokenType = string.IsNullOrEmpty(token.TokenType) ? "Bearer" : token.TokenType,
            Token = request.Raw ? token.Token : null,
        };
    }

    /// <summary>Whether a held token has enough life left to be worth handing out.</summary>
    /// <remarks>
    /// RefreshOn wins when the issuer set it: it is the issuer saying when it wants to be asked
    /// again, which is better information than a constant guessed at this end. The skew is the
    /// fallback for the common case where it is absent.
    /// </remarks>
    private static bool IsUsable(AccessToken token, DateTimeOffset now) =>
        token.RefreshOn is DateTimeOffset refreshOn
            ? now < refreshOn
            : now < token.ExpiresOn - RefreshSkew;

    private AccessToken Fetch(string scope, string? tenant)
    {
        TokenCredential credential = _credentials.GetOrAdd(tenant ?? string.Empty, _ => _factory(tenant));

        // parentRequestId is required by every overload -- there is no scopes-only constructor --
        // and null is what the SDK's own callers pass when there is no ambient request to
        // correlate with.
        TokenRequestContext context = new([scope], null, null, tenant);

        try
        {
            return credential.GetToken(context, CancellationToken.None);
        }
        catch (CredentialUnavailableException ex)
        {
            // The credential could not even be attempted: az missing, or nobody signed in.
            // Both are fixed by the caller at a prompt, so say which one and what to type.
            throw new GraphException(
                $"The Azure CLI could not supply a token: {ex.Message}{Environment.NewLine}" +
                "Run 'az login' (or install the Azure CLI) and try again. Janet borrows the CLI's " +
                "sign-in rather than holding a credential of its own.");
        }
        catch (AuthenticationFailedException ex)
        {
            // Reached Entra and was refused. Usually the wrong tenant, or an account without
            // access to the resource being scoped for -- neither of which is fixed by retrying.
            throw new GraphException(
                $"Azure refused the token request for scope '{scope}'" +
                (tenant is null ? string.Empty : $" in tenant '{tenant}'") +
                $": {ex.Message}");
        }
    }
}

/// <summary>
/// The process-wide token cache, and the resource vocabulary it resolves against.
/// </summary>
public static class AzToken
{
    public const string DefaultResource = "arm";

    /// <summary>
    /// The scopes worth having a short name for.
    /// </summary>
    /// <remarks>
    /// A fixed catalog rather than free text, for the reason DESIGN-NOTES section 12 gives: the
    /// alias is a discriminator, and the set of things it can select is knowable. A caller who
    /// needs something not on this list passes the literal scope and nothing is in their way --
    /// so the catalog is a convenience with an escape hatch, not a gate.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> Resources =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["arm"] = "https://management.azure.com/.default",
            ["management"] = "https://management.azure.com/.default",
            ["storage"] = "https://storage.azure.com/.default",
            ["graph"] = "https://graph.microsoft.com/.default",
            ["keyvault"] = "https://vault.azure.net/.default",
            ["sql"] = "https://database.windows.net/.default",
            ["servicebus"] = "https://servicebus.azure.net/.default",
            ["cosmos"] = "https://cosmos.azure.com/.default",
        };

    private static readonly AzTokenCache Shared = new();

    /// <summary>Acquire through the process-wide cache. This is what the front ends call.</summary>
    public static AzTokenResult Acquire(AzTokenRequest request) => Shared.Acquire(request);

    /// <summary>
    /// Turns what the caller typed into a scope, and reports which alias got them there.
    /// </summary>
    /// <returns>The resolved scope, and the alias it came from, or null for a literal scope.</returns>
    public static (string Scope, string? Resource) ResolveScope(string requested)
    {
        if (Resources.TryGetValue(requested, out string? mapped))
        {
            return (mapped, requested.ToLowerInvariant());
        }

        // Anything that looks like a URI is taken as written. This is the escape hatch, and it
        // has to come before the refusal below or a caller with an unlisted resource is stuck.
        if (requested.Contains("://", StringComparison.Ordinal))
        {
            // Azure wants a scope, not a resource id. Appending the suffix for a caller who
            // passed a bare resource is the difference between a token and an opaque AADSTS
            // error about an invalid scope.
            return requested.EndsWith("/.default", StringComparison.OrdinalIgnoreCase)
                ? (requested, null)
                : ($"{requested.TrimEnd('/')}/.default", null);
        }

        throw new GraphException(
            $"'{requested}' is neither a known resource alias nor a scope URI. " +
            $"Known aliases: {string.Join(", ", Resources.Keys.Order(StringComparer.Ordinal))}. " +
            "Anything else must be a full scope, e.g. https://management.azure.com/.default.");
    }
}
