using System.Text.Json.Nodes;
using Azure.Core;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The token cache, tested without ever reaching the Azure CLI.
/// </summary>
/// <remarks>
/// Every test here drives a stub credential and a controlled clock. That is not only for speed:
/// a test that called the real `az` would assert on the machine's login state, so it would pass
/// on this box, fail on a build agent, and start failing here the day a token expired -- which
/// is a test that reports the environment rather than the code.
///
/// What is genuinely NOT covered, and worth saying rather than implying: no test here proves
/// that AzureCliCredential is wired up correctly, because the stub replaces exactly that seam.
/// The first real `az` call is the only thing that proves it.
/// </remarks>
public class AzTokenTests
{
    /// <summary>A credential that hands out predictable tokens and counts how often it was asked.</summary>
    private sealed class StubCredential(Func<int, AccessToken> issue) : TokenCredential
    {
        public int Calls { get; private set; }

        public List<TokenRequestContext> Contexts { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Contexts.Add(requestContext);
            return issue(++Calls);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly DateTimeOffset Start = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static (AzTokenCache Cache, StubCredential Credential, TestClock Clock) Harness(TimeSpan? lifetime = null)
    {
        TestClock clock = new(Start);
        TimeSpan life = lifetime ?? TimeSpan.FromHours(1);
        StubCredential credential = new(n => new AccessToken($"token-{n}", clock.GetUtcNow() + life));

        return (new AzTokenCache(_ => credential, clock), credential, clock);
    }

    // ---- Scope resolution ---------------------------------------------------

    [Theory]
    [InlineData("arm", "https://management.azure.com/.default")]
    [InlineData("ARM", "https://management.azure.com/.default")]
    [InlineData("storage", "https://storage.azure.com/.default")]
    [InlineData("graph", "https://graph.microsoft.com/.default")]
    public void AnAliasResolvesToItsScope(string alias, string expected)
    {
        (string scope, string? resource) = AzToken.ResolveScope(alias);

        Assert.Equal(expected, scope);
        Assert.Equal(alias.ToLowerInvariant(), resource);
    }

    [Fact]
    public void AFullScopeIsPassedThroughAndReportsNoAlias()
    {
        (string scope, string? resource) = AzToken.ResolveScope("https://vault.azure.net/.default");

        Assert.Equal("https://vault.azure.net/.default", scope);

        // Null is "not applicable", not "unknown" -- a literal scope has no alias by definition.
        Assert.Null(resource);
    }

    [Fact]
    public void ABareResourceUriGainsTheDefaultSuffix()
    {
        // Passing a resource id where a scope is wanted is the single most common way to get an
        // opaque AADSTS error instead of a token, and the fix is mechanical.
        Assert.Equal("https://management.azure.com/.default", AzToken.ResolveScope("https://management.azure.com").Scope);
        Assert.Equal("https://management.azure.com/.default", AzToken.ResolveScope("https://management.azure.com/").Scope);
    }

    [Fact]
    public void AnUnknownAliasIsRefusedWithTheListRatherThanGuessedAt()
    {
        GraphException ex = Assert.Throws<GraphException>(() => AzToken.ResolveScope("blobstorage"));

        Assert.Contains("blobstorage", ex.Message);

        // The refusal has to carry the vocabulary, or the caller's next move is a second guess.
        Assert.Contains("storage", ex.Message);
        Assert.Contains("keyvault", ex.Message);
    }

    // ---- Caching ------------------------------------------------------------

    [Fact]
    public void TheSecondAskIsServedFromTheCache()
    {
        (AzTokenCache cache, StubCredential credential, _) = Harness();

        AzTokenResult first = cache.Acquire(new AzTokenRequest { Scope = "arm", Raw = true });
        AzTokenResult second = cache.Acquire(new AzTokenRequest { Scope = "arm", Raw = true });

        Assert.False(first.Cached);
        Assert.True(second.Cached);
        Assert.Equal("token-1", second.Token);
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public void TwoAliasesForOneScopeShareACacheEntry()
    {
        // arm and management are the same resource. Keying on the alias rather than the resolved
        // scope would quietly double every acquisition for callers who spell it differently.
        (AzTokenCache cache, StubCredential credential, _) = Harness();

        cache.Acquire(new AzTokenRequest { Scope = "arm" });
        AzTokenResult viaManagement = cache.Acquire(new AzTokenRequest { Scope = "management" });

        Assert.True(viaManagement.Cached);
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public void DifferentScopesDoNotShareAToken()
    {
        (AzTokenCache cache, StubCredential credential, _) = Harness();

        cache.Acquire(new AzTokenRequest { Scope = "arm" });
        AzTokenResult storage = cache.Acquire(new AzTokenRequest { Scope = "storage" });

        Assert.False(storage.Cached);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public void DifferentTenantsDoNotShareAToken()
    {
        // The failure this prevents is a 401 a long way from here: a token for the wrong
        // directory looks entirely valid until the service rejects it.
        (AzTokenCache cache, StubCredential credential, _) = Harness();

        cache.Acquire(new AzTokenRequest { Scope = "arm", Tenant = "tenant-a" });
        AzTokenResult other = cache.Acquire(new AzTokenRequest { Scope = "arm", Tenant = "tenant-b" });

        Assert.False(other.Cached);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public void ATokenNearingExpiryIsReplacedRatherThanHandedOut()
    {
        (AzTokenCache cache, StubCredential credential, TestClock clock) = Harness(TimeSpan.FromMinutes(10));

        cache.Acquire(new AzTokenRequest { Scope = "arm" });

        // Six minutes in, four are left -- inside the five-minute skew, so it is no longer worth
        // handing to a caller who may be about to start something slow.
        clock.Advance(TimeSpan.FromMinutes(6));

        AzTokenResult refreshed = cache.Acquire(new AzTokenRequest { Scope = "arm", Raw = true });

        Assert.False(refreshed.Cached);
        Assert.Equal("token-2", refreshed.Token);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public void ATokenComfortablyInsideItsLifetimeIsReused()
    {
        (AzTokenCache cache, StubCredential credential, TestClock clock) = Harness(TimeSpan.FromMinutes(10));

        cache.Acquire(new AzTokenRequest { Scope = "arm" });
        clock.Advance(TimeSpan.FromMinutes(4));

        Assert.True(cache.Acquire(new AzTokenRequest { Scope = "arm" }).Cached);
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public void RefreshOnWinsOverTheSkewWhenTheIssuerSetIt()
    {
        // The issuer saying when to come back is better information than a constant guessed at
        // this end, so it has to take precedence rather than being ANDed with it.
        TestClock clock = new(Start);
        StubCredential credential = new(n => new AccessToken(
            $"token-{n}",
            clock.GetUtcNow() + TimeSpan.FromHours(1),
            clock.GetUtcNow() + TimeSpan.FromMinutes(2)));

        AzTokenCache cache = new(_ => credential, clock);

        cache.Acquire(new AzTokenRequest { Scope = "arm" });

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(cache.Acquire(new AzTokenRequest { Scope = "arm" }).Cached);

        // Past RefreshOn but still 57 minutes from expiry: the skew alone would have reused it.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(cache.Acquire(new AzTokenRequest { Scope = "arm" }).Cached);
    }

    [Fact]
    public void RefreshForcesANewTokenPastALiveCachedOne()
    {
        (AzTokenCache cache, StubCredential credential, _) = Harness();

        cache.Acquire(new AzTokenRequest { Scope = "arm" });
        AzTokenResult forced = cache.Acquire(new AzTokenRequest { Scope = "arm", Refresh = true, Raw = true });

        Assert.False(forced.Cached);
        Assert.Equal("token-2", forced.Token);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public void TheTenantIsPassedToTheCredentialAndNotJustUsedAsACacheKey()
    {
        // A tenant that only ever reached the cache key would silently return the default
        // directory's token under a label claiming otherwise.
        (AzTokenCache cache, StubCredential credential, _) = Harness();

        cache.Acquire(new AzTokenRequest { Scope = "arm", Tenant = "tenant-a" });

        Assert.Equal("tenant-a", credential.Contexts[0].TenantId);
        Assert.Equal(["https://management.azure.com/.default"], credential.Contexts[0].Scopes);
    }

    // ---- What comes back ----------------------------------------------------

    [Fact]
    public void TheTokenIsWithheldUnlessItWasAskedFor()
    {
        (AzTokenCache cache, _, _) = Harness();

        Assert.Null(cache.Acquire(new AzTokenRequest { Scope = "arm" }).Token);
        Assert.Equal("token-1", cache.Acquire(new AzTokenRequest { Scope = "arm", Raw = true }).Token);
    }

    [Fact]
    public void AWithheldTokenIsAnABSENTKEYRatherThanANull()
    {
        // The distinction the format turns on: absent says "you did not ask", null would say
        // "there is no token", and those want different actions from whoever hit the difference.
        JsonObject withheld = JsonNode.Parse(
            AzTokenJson.Serialize(Harness().Cache.Acquire(new AzTokenRequest { Scope = "arm" })))!.AsObject();

        Assert.False(withheld.ContainsKey("token"));

        JsonObject asked = JsonNode.Parse(
            AzTokenJson.Serialize(Harness().Cache.Acquire(new AzTokenRequest { Scope = "arm", Raw = true })))!.AsObject();

        Assert.Equal("token-1", (string?)asked["token"]);
    }

    [Fact]
    public void TheEnvelopeStampsItsContract()
    {
        JsonObject envelope = JsonNode.Parse(
            AzTokenJson.Serialize(Harness().Cache.Acquire(new AzTokenRequest { Scope = "arm" })))!.AsObject();

        Assert.Equal(2, (int)envelope["contract"]!);
        Assert.Equal("arm", (string?)envelope["requested"]);
        Assert.Equal("https://management.azure.com/.default", (string?)envelope["scope"]);
        Assert.Equal("arm", (string?)envelope["resource"]);
        Assert.Equal("Bearer", (string?)envelope["tokenType"]);
    }

    [Fact]
    public void ALiteralScopeSerializesItsAliasAsNullRatherThanOmittingIt()
    {
        JsonObject envelope = JsonNode.Parse(AzTokenJson.Serialize(
            Harness().Cache.Acquire(new AzTokenRequest { Scope = "https://custom.example/.default" })))!.AsObject();

        // Present-and-null, unlike token: the schema requires it, because "no alias" is a real
        // answer to a question that was asked, not a value the caller declined to receive.
        Assert.True(envelope.ContainsKey("resource"));
        Assert.Null((string?)envelope["resource"]);
    }

    [Fact]
    public void ExpiryIsReportedInUtcAndInSecondsThatAgreeWithIt()
    {
        (AzTokenCache cache, _, _) = Harness(TimeSpan.FromMinutes(42));

        AzTokenResult result = cache.Acquire(new AzTokenRequest { Scope = "arm" });

        Assert.Equal(42 * 60, result.ExpiresInSeconds);
        Assert.Equal(Start + TimeSpan.FromMinutes(42), result.ExpiresOn);

        JsonObject envelope = JsonNode.Parse(AzTokenJson.Serialize(result))!.AsObject();
        Assert.EndsWith("Z", (string)envelope["expiresOn"]!);
    }

    [Fact]
    public void AnAlreadyExpiredTokenReportsZeroSecondsRatherThanANegativeNumber()
    {
        TestClock clock = new(Start);

        // A machine with a wrong clock is the real case: the issuer's expiry is already behind us.
        StubCredential credential = new(n => new AccessToken($"token-{n}", clock.GetUtcNow() - TimeSpan.FromMinutes(5)));
        AzTokenCache cache = new(_ => credential, clock);

        Assert.Equal(0, cache.Acquire(new AzTokenRequest { Scope = "arm" }).ExpiresInSeconds);
    }

    [Fact]
    public void TheRenderedViewNamesTheFlagThatWouldShowTheToken()
    {
        // Without this the absence reads as a failure, and the reader's next move is a bug report
        // rather than a flag.
        string rendered = AzTokenJson.Render(Harness().Cache.Acquire(new AzTokenRequest { Scope = "arm" }));

        Assert.Contains("--raw", rendered);
        Assert.DoesNotContain("token-1", rendered);
    }

    [Fact]
    public void TheRenderedViewShowsTheTokenOnceItIsAskedFor()
    {
        string rendered = AzTokenJson.Render(Harness().Cache.Acquire(new AzTokenRequest { Scope = "arm", Raw = true }));

        Assert.Contains("token-1", rendered);
    }

    // ---- Borrowing a server's answer ---------------------------------------

    [Fact]
    public void AnAnswerComputedHereSaysSo()
    {
        // The default has to be 'process', because everything that does not go over a wire is
        // computed here -- including the server's own tool call.
        Assert.Equal(ServedBy.Process, Harness().Cache.Acquire(new AzTokenRequest { Scope = "arm" }).ServedBy);

        JsonObject envelope = JsonNode.Parse(
            AzTokenJson.Serialize(Harness().Cache.Acquire(new AzTokenRequest { Scope = "arm" })))!.AsObject();

        Assert.Equal("process", (string?)envelope["servedBy"]);
    }

    [Fact]
    public void AnEnvelopeSurvivesTheRoundTripTheCliPutsItThrough()
    {
        // The CLI receives a serialized envelope and parses it back so it can render --text and
        // stamp servedBy from the same object a local answer produces. If that round trip loses
        // a field, the borrowed path silently starts describing a different token.
        AzTokenResult original = Harness().Cache.Acquire(new AzTokenRequest { Scope = "arm", Raw = true });
        AzTokenResult parsed = AzTokenJson.Parse(AzTokenJson.Serialize(original));

        Assert.Equal(original.Requested, parsed.Requested);
        Assert.Equal(original.Scope, parsed.Scope);
        Assert.Equal(original.Resource, parsed.Resource);
        Assert.Equal(original.TokenType, parsed.TokenType);
        Assert.Equal(original.ExpiresInSeconds, parsed.ExpiresInSeconds);
        Assert.Equal(original.Cached, parsed.Cached);
        Assert.Equal(original.Token, parsed.Token);
        Assert.Equal(original.ExpiresOn.ToUniversalTime(), parsed.ExpiresOn.ToUniversalTime());
    }

    [Fact]
    public void AWithheldTokenStaysWithheldAcrossTheRoundTrip()
    {
        AzTokenResult parsed = AzTokenJson.Parse(
            AzTokenJson.Serialize(Harness().Cache.Acquire(new AzTokenRequest { Scope = "arm" })));

        Assert.Null(parsed.Token);
    }

    [Fact]
    public void AParsedEnvelopeStampedAsBorrowedRendersAsBorrowed()
    {
        // What the CLI actually does with the parsed result. The rendered line has to say the
        // answer came from a server, or an implausibly fast CLI call looks like a bug.
        AzTokenResult borrowed = AzTokenJson.Parse(
            AzTokenJson.Serialize(Harness().Cache.Acquire(new AzTokenRequest { Scope = "arm" })))
            with { ServedBy = ServedBy.Server };

        Assert.Contains("janet-mcp", AzTokenJson.Render(borrowed));
        Assert.Equal("server", (string?)JsonNode.Parse(AzTokenJson.Serialize(borrowed))!["servedBy"]);
    }

    [Fact]
    public void TheServersRefusalIsForwardedRatherThanReDerived()
    {
        // A 400 from the server carries the message this process would have produced anyway.
        // Parsing it back into the same GraphException is what stops the CLI retrying locally to
        // reach the identical error a second and slower time.
        GraphException ex = Assert.Throws<GraphException>(
            () => AzTokenJson.Parse("""{"error":"'nope' is neither a known resource alias nor a scope URI."}"""));

        Assert.Contains("'nope' is neither", ex.Message);
    }

    [Fact]
    public void AMalformedEnvelopeThrowsRatherThanDefaulting()
    {
        // A default here would describe a token nobody issued -- expiring at the epoch, with no
        // scope -- and the caller would act on it.
        Assert.Throws<GraphException>(() => AzTokenJson.Parse("""{"scope":"https://x/.default"}"""));
        Assert.Throws<GraphException>(() => AzTokenJson.Parse("[]"));
    }

    [Fact]
    public void APortIsResolvedFromTheConfigThatDeclaresIt()
    {
        // The server and the CLI must agree on which port means which graph, or the CLI dials an
        // address nothing answers on and silently falls back forever.
        string directory = Path.Combine(Path.GetTempPath(), "janet-link", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(
                Path.Combine(directory, ".mcp.json"),
                """{ "mcpServers": { "janet": { "type": "http", "url": "http://127.0.0.1:7717/" } } }""");

            Assert.Equal(7717, ServerLink.ResolvePort(Path.Combine(directory, "research.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WithNoConfigThePortIsDerivedRatherThanGuessedAtZero()
    {
        string directory = Path.Combine(Path.GetTempPath(), "janet-link", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            int port = ServerLink.ResolvePort(Path.Combine(directory, "research.json"));

            // Derivation is a bootstrap suggestion, but it still has to be a usable port: a zero
            // or a privileged one would make the fallback path the only path, forever.
            Assert.InRange(port, 1024, 65535);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NothingListeningIsNotAnErrorItIsJustNoServer()
    {
        // The whole safety property in one test: an absent server must produce a "do it
        // yourself" answer, never an exception. A cache that can take the CLI down is worse
        // than no cache, and hooks call the CLI.
        string directory = Path.Combine(Path.GetTempPath(), "janet-link", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            // A port nothing serves, and starting is refused, so this cannot wander off and
            // spawn a real server from a test run.
            File.WriteAllText(
                Path.Combine(directory, ".mcp.json"),
                """{ "mcpServers": { "janet": { "type": "http", "url": "http://127.0.0.1:7799/" } } }""");

            LinkOutcome outcome = ServerLink.TryFetch(
                Path.Combine(directory, "research.json"), "az/token?scope=arm", allowStart: false);

            Assert.Equal(ServedBy.Process, outcome.ServedBy);
            Assert.Null(outcome.Body);

            // The reason has to be legible, or an invisible fallback is how this quietly stops
            // working for months.
            Assert.Contains("7799", outcome.Detail);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- Failure ------------------------------------------------------------

    [Fact]
    public void ANotSignedInCredentialBecomesAnActionableRefusal()
    {
        // GraphException is what the MCP filter surfaces to a caller; anything else arrives as
        // "An error occurred invoking 'az_token'." and nothing more.
        AzTokenCache cache = new(
            _ => new StubCredential(_ => throw new Azure.Identity.CredentialUnavailableException(
                "Please run 'az login' to set up an account.")),
            new TestClock(Start));

        GraphException ex = Assert.Throws<GraphException>(() => cache.Acquire(new AzTokenRequest { Scope = "arm" }));

        Assert.Contains("az login", ex.Message);
    }

    [Fact]
    public void ARefusalByAzureNamesTheScopeItWasRefusedFor()
    {
        AzTokenCache cache = new(
            _ => new StubCredential(_ => throw new Azure.Identity.AuthenticationFailedException("AADSTS500011")),
            new TestClock(Start));

        GraphException ex = Assert.Throws<GraphException>(
            () => cache.Acquire(new AzTokenRequest { Scope = "storage", Tenant = "tenant-a" }));

        Assert.Contains("https://storage.azure.com/.default", ex.Message);
        Assert.Contains("tenant-a", ex.Message);
        Assert.Contains("AADSTS500011", ex.Message);
    }

    [Fact]
    public void AFailedAcquisitionIsNotCachedAsASuccess()
    {
        int calls = 0;
        TestClock clock = new(Start);

        AzTokenCache cache = new(
            _ => new StubCredential(_ =>
                ++calls == 1
                    ? throw new Azure.Identity.CredentialUnavailableException("not logged in")
                    : new AccessToken("token-good", clock.GetUtcNow() + TimeSpan.FromHours(1))),
            clock);

        Assert.Throws<GraphException>(() => cache.Acquire(new AzTokenRequest { Scope = "arm" }));

        // The retry after `az login` has to be able to succeed. A cache that stored the failure,
        // or that was left holding a torn entry, would make the fix look like it did not work.
        AzTokenResult recovered = cache.Acquire(new AzTokenRequest { Scope = "arm", Raw = true });

        Assert.Equal("token-good", recovered.Token);
        Assert.False(recovered.Cached);
    }
}
