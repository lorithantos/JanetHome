using Janet.Core;
using Xunit;
using Side = Janet.Tests.StorageCopyWorld.Side;

namespace Janet.Tests;

/// <summary>
/// The storage copy plan against two canned accounts: what is dropped, what is wiped, what blocks,
/// and that the hash moves with anything the plan read.
/// </summary>
public sealed class StorageCopyTests : IDisposable
{
    private const string Src = "test-salon-one";
    private const string Tgt = "staging-salon";

    private readonly string _spec = Path.Combine(Path.GetTempPath(), $"janet-storage-copy-{Guid.NewGuid():N}.json");

    public StorageCopyTests() => File.WriteAllText(_spec, StorageCopyFixtures.Neelam);

    public void Dispose() => File.Delete(_spec);

    private StorageCopyPlanResult Plan(StorageCopyWorld world, string mode = "refresh", string? rule = null, string pair = "priya-to-staging", string? spec = null) =>
        world.Copier().Plan(new StorageCopyRequest
        {
            Spec = spec ?? _spec,
            Pair = pair,
            Mode = mode,
            Rule = rule,
            Subscription = StubAzure.Sub,
        });

    [Fact]
    public void UndoneSavesAndTheirProofreadsAreDropped()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam()
            .Blob(Side.Source, Src, "drafts/A/s1.json")
            .UndoneBlob(Side.Source, Src, "drafts/B/s2.json")
            .Blob(Side.Source, Src, "proofread/A/s1.json")
            .Blob(Side.Source, Src, "proofread/B/s2.json")
            .Blob(Side.Source, Src, "proofread/C/s3.json");

        StorageCopyPlanResult plan = Plan(world, rule: "proofread/{id}/{stamp}.json");

        Assert.Equal(["proofread/A/s1.json"], plan.Names!.Copy);
        Assert.Equal([new ReasonCount("noLiveSave", 2), new ReasonCount("undone", 1)], plan.DroppedByReason);
        Assert.Equal(3, plan.DroppedCount);
        Assert.True(plan.ReadyToApply);
    }

    [Fact]
    public void TheFirstMatchingRuleWins()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam().Blob(Side.Source, Src, "proofread/images/h.json", length: 7);

        StorageCopyPlanResult plan = Plan(world);

        RuleCopy only = Assert.Single(plan.CopyByRule);
        Assert.Equal(new RuleCopy("proofread/images/{hash}.json", 1, 7), only);
    }

    [Fact]
    public void ApprovalRowsFollowTheirSave()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam()
            .Blob(Side.Source, Src, "drafts/A/s1.json")
            .UndoneBlob(Side.Source, Src, "drafts/B/s2.json")
            .Entity(Side.Source, "approvals", Src, "A_s1")
            .Entity(Side.Source, "approvals", Src, "B_s2");

        StorageCopyPlanResult plan = Plan(world, rule: "approvals");

        Assert.Equal(1, plan.CopyByRule.Single(r => r.Rule == "approvals").Count);
        Assert.Equal(1, plan.CopyEntities);
        Assert.Equal([$"{Tgt}/A_s1"], plan.Names!.Copy);
    }

    [Fact]
    public void AnUnmatchedSourceBlobBlocks()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam().Blob(Side.Source, Src, "stray/x.json");

        StorageCopyPlanResult plan = Plan(world);

        Assert.Equal(1, plan.UnmatchedCount);
        Assert.Equal(["stray/x.json"], plan.UnmatchedNames);
        Assert.False(plan.ReadyToApply);
        Assert.Contains("match no rule", Assert.Single(plan.Blockers));
    }

    [Fact]
    public void TheTargetWipeStaysInScope()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam()
            .Blob(Side.Target, "settings", $"settings/{Tgt}/s.json")
            .Blob(Side.Target, "settings", "_standard-baseline/b.json");

        StorageCopyPlanResult plan = Plan(world, rule: "settings/{client}/{stamp}.json");

        Assert.Equal([new RuleCount("settings/{client}/{stamp}.json", 1)], plan.DeleteByRule);
        Assert.Equal(1, plan.DeleteBlobs);
        Assert.Equal([$"settings/{Tgt}/s.json"], plan.Names!.Delete);
    }

    [Fact]
    public void ATargetBlobNoRuleMatchesIsNeverWiped()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam()
            .Blob(Side.Target, Tgt, "stray/x.json")
            .Blob(Side.Target, Tgt, "images/old.png");

        StorageCopyPlanResult plan = Plan(world);

        Assert.Equal(1, plan.DeleteBlobs);
        Assert.Equal([new RuleCount("images/{name}", 1)], plan.DeleteByRule);
        Assert.Empty(Plan(world, rule: "images/{name}").Names!.Copy);
        Assert.DoesNotContain("stray/x.json", Plan(world, rule: "images/{name}").Names!.Delete);
    }

    [Fact]
    public void ACopyFalseRuleNeitherCopiesNorWipes()
    {
        const string spec = """
            {
              "pairs": [ { "name": "p", "source": { "account": "src", "client": "test-salon-one" },
                           "target": { "account": "tgt", "client": "staging-salon" } } ],
              "containers": [ { "name": "{client}", "rules": [
                  { "match": "keep/{n}.json" },
                  { "match": "skip/{n}.json", "copy": false } ] } ],
              "tables": []
            }
            """;
        string path = Path.Combine(Path.GetTempPath(), $"janet-storage-copy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, spec);
        try
        {
            StorageCopyWorld world = new StorageCopyWorld().Container(Side.Source, Src).Container(Side.Target, Tgt)
                .Blob(Side.Source, Src, "keep/a.json")
                .Blob(Side.Source, Src, "skip/b.json")
                .Blob(Side.Target, Tgt, "keep/old.json")
                .Blob(Side.Target, Tgt, "skip/c.json");

            StorageCopyPlanResult plan = Plan(world, pair: "p", spec: path);

            Assert.Equal(1, plan.CopyBlobs);
            Assert.Equal([new RuleCopy("keep/{n}.json", 1, 10)], plan.CopyByRule);
            Assert.Equal(1, plan.DeleteBlobs);
            Assert.Equal([new RuleCount("keep/{n}.json", 1)], plan.DeleteByRule);
            Assert.Equal(0, plan.DroppedCount);
            Assert.Equal(0, plan.UnmatchedCount);
            Assert.True(plan.ReadyToApply);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheSettingsCopyIsRenamedToTheTargetClient()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam().Blob(Side.Source, "settings", $"settings/{Src}/s.json");

        StorageCopyPlanResult plan = Plan(world, rule: "settings/{client}/{stamp}.json");

        Assert.Equal([$"settings/{Tgt}/s.json"], plan.Names!.Copy);
    }

    [Fact]
    public void RefreshFromAnEmptySourceOnlyDeletes()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam()
            .Blob(Side.Target, Tgt, "drafts/A/s1.json")
            .Blob(Side.Target, Tgt, "images/a.png")
            .Blob(Side.Target, Tgt, "catalog/c1.json")
            .Entity(Side.Target, "knownItems", Tgt, "k1")
            .Entity(Side.Target, "knownItems", Tgt, "k2");

        StorageCopyPlanResult plan = Plan(world);

        Assert.Equal(0, plan.CopyBlobs);
        Assert.Equal(0, plan.CopyEntities);
        Assert.Equal(3, plan.DeleteBlobs);
        Assert.Equal(2, plan.DeleteEntities);
        Assert.True(plan.ReadyToApply);
    }

    [Theory]
    [InlineData("targetContainer", "target container 'staging-salon'")]
    [InlineData("targetTable", "target table 'approvals'")]
    [InlineData("sourceContainer", "source container 'test-salon-one'")]
    public void AMissingContainerOrTableIsABlocker(string missing, string expected)
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam();
        _ = missing switch
        {
            "targetContainer" => world.MissingContainer(Side.Target, Tgt),
            "targetTable" => world.MissingTable(Side.Target, "approvals"),
            _ => world.MissingContainer(Side.Source, Src),
        };

        StorageCopyPlanResult plan = Plan(world);

        Assert.False(plan.ReadyToApply);
        Assert.Contains(plan.Blockers, b => b.Contains(expected, StringComparison.OrdinalIgnoreCase) && b.Contains("does not exist"));
    }

    [Fact]
    public void ClearReadsOnlyTheTarget()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam()
            .Blob(Side.Source, Src, "drafts/A/s1.json")
            .Blob(Side.Target, Tgt, "drafts/A/s1.json")
            .Entity(Side.Target, "knownItems", Tgt, "k1");

        StorageCopyPlanResult plan = Plan(world, "clear");

        Assert.DoesNotContain(world.Stub.Requests, r => r.Url.Contains("//src.", StringComparison.Ordinal));
        Assert.Equal(0, plan.CopyBlobs);
        Assert.Equal(0, plan.CopyEntities);
        Assert.Equal(0, plan.CopyBytes);
        Assert.Equal(1, plan.DeleteBlobs);
        Assert.Equal(1, plan.DeleteEntities);
    }

    [Fact]
    public void AFailedGuardSendsNothing()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam();

        Assert.Throws<GraphException>(() => Plan(world, pair: "nope"));

        Assert.Empty(world.Stub.Requests);
    }

    [Fact]
    public void AnUnknownModeSendsNothing()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam();

        Assert.Throws<GraphException>(() => Plan(world, "wipe"));

        Assert.Empty(world.Stub.Requests);
    }

    [Fact]
    public void TheHashMovesWithAnyETag()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam()
            .Blob(Side.Source, Src, "images/a.png", etag: "\"0xA\"")
            .Blob(Side.Target, Tgt, "images/old.png", etag: "\"0xB\"");

        string first = Plan(world).PlanHash;
        string again = Plan(world).PlanHash;
        world.SetETag(Side.Source, Src, "images/a.png", "\"0xC\"");
        string afterSource = Plan(world).PlanHash;
        world.SetETag(Side.Target, Tgt, "images/old.png", "\"0xD\"");
        string afterTarget = Plan(world).PlanHash;

        Assert.Equal(first, again);
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.NotEqual(first, afterSource);
        Assert.NotEqual(afterSource, afterTarget);
    }

    [Fact]
    public void RuleListsThatRulesNames()
    {
        StorageCopyWorld world = StorageCopyWorld.Neelam()
            .Blob(Side.Source, Src, "images/b.png")
            .Blob(Side.Source, Src, "images/a.png")
            .Blob(Side.Target, Tgt, "images/old.png")
            .Blob(Side.Source, Src, "drafts/A/s1.json");

        StorageCopyPlanResult named = Plan(world, rule: "images/{name}");
        StorageCopyPlanResult unnamed = Plan(world);

        Assert.Equal(["images/a.png", "images/b.png"], named.Names!.Copy);
        Assert.Equal(["images/old.png"], named.Names.Delete);
        Assert.Null(unnamed.Names);

        GraphException ex = Assert.Throws<GraphException>(() => Plan(world, rule: "nope"));
        Assert.Contains("images/{name}", ex.Message);
        Assert.Contains("approvals", ex.Message);
    }

    private StorageCopyApplyResult Apply(StorageCopyWorld world, string? hash, string mode = "refresh") =>
        world.Copier().Apply(new StorageCopyRequest
        {
            Spec = _spec,
            Pair = "priya-to-staging",
            Mode = mode,
            Subscription = StubAzure.Sub,
            PlanHash = hash,
        });

    /// <summary>Source and target each hold blobs and rows, so a refresh deletes and copies both kinds.</summary>
    private static StorageCopyWorld Populated() => StorageCopyWorld.Neelam()
        .Blob(Side.Source, Src, "drafts/A/s1.json", length: 5, metadata: new Dictionary<string, string> { ["title"] = "Spring" })
        .Blob(Side.Source, Src, "images/logo.png", length: 7, contentType: "image/png")
        .Blob(Side.Source, Src, "images/z.png", length: 11, contentType: "image/png")
        .Entity(Side.Source, "knownItems", Src, "k1")
        .Blob(Side.Target, Tgt, "drafts/old/s0.json")
        .Entity(Side.Target, "knownItems", Tgt, "k0");

    private static IEnumerable<StubAzure.Recorded> Writes(StorageCopyWorld world) =>
        world.Stub.Requests.Where(r => r.Method != HttpMethod.Get);

    [Fact]
    public void TheSourceIsNeverWritten()
    {
        StorageCopyWorld world = Populated().AcceptWrites();

        Assert.Equal("applied", Apply(world, Plan(world).PlanHash).Outcome);
        Assert.Equal("cleared", Apply(world, Plan(world, "clear").PlanHash, "clear").Outcome);

        List<StubAzure.Recorded> writes = [.. Writes(world)];
        Assert.NotEmpty(writes);
        Assert.All(writes, w => Assert.StartsWith("tgt.", new Uri(w.Url).Host, StringComparison.Ordinal));
    }

    [Fact]
    public void AStaleHashWritesNothing()
    {
        StorageCopyWorld world = Populated().AcceptWrites();
        string old = Plan(world).PlanHash;
        world.SetETag(Side.Source, Src, "drafts/A/s1.json", "\"0xCHANGED\"");

        StorageCopyApplyResult result = Apply(world, old);

        Assert.Equal("stale", result.Outcome);
        Assert.NotEqual(old, result.PlanHash);
        Assert.Empty(Writes(world));
        Assert.Equal(0, result.DeletedBlobs + result.CopiedBlobs + result.DeletedEntities + result.CopiedEntities);
    }

    [Fact]
    public void ABlockedPlanWritesNothing()
    {
        StorageCopyWorld world = Populated().Blob(Side.Source, Src, "weird/x.bin").AcceptWrites();
        StorageCopyPlanResult plan = Plan(world);
        Assert.False(plan.ReadyToApply);

        StorageCopyApplyResult result = Apply(world, plan.PlanHash);

        Assert.Equal("blocked", result.Outcome);
        Assert.Empty(Writes(world));
    }

    [Fact]
    public void ApplyWithoutAHashIsRefused()
    {
        StorageCopyWorld world = Populated().AcceptWrites();

        Assert.Throws<GraphException>(() => Apply(world, null));
        Assert.Throws<GraphException>(() => Apply(world, ""));

        Assert.Empty(world.Stub.Requests);
    }

    [Fact]
    public void RefreshDeletesThenCopies()
    {
        StorageCopyWorld world = Populated().AcceptWrites();
        StorageCopyPlanResult plan = Plan(world);

        StorageCopyApplyResult result = Apply(world, plan.PlanHash);

        List<StubAzure.Recorded> writes = [.. Writes(world)];
        Assert.Equal(HttpMethod.Delete, writes[0].Method);
        int firstPut = writes.FindIndex(w => w.Method == HttpMethod.Put);
        Assert.DoesNotContain(writes.Skip(firstPut), w => w.Method == HttpMethod.Delete);
        Assert.Equal("applied", result.Outcome);
        Assert.Equal(plan.PlanHash, result.PlanHash);
        Assert.Equal((plan.DeleteBlobs, plan.DeleteEntities), (result.DeletedBlobs, result.DeletedEntities));
        Assert.Equal((plan.CopyBlobs, plan.CopyBytes, plan.CopyEntities), (result.CopiedBlobs, result.CopiedBytes, result.CopiedEntities));
        Assert.Equal((1, 1, 3, 23L, 1), (result.DeletedBlobs, result.DeletedEntities, result.CopiedBlobs, result.CopiedBytes, result.CopiedEntities));
        Assert.Null(result.Failure);
        Assert.NotNull(result.CopiedAt);
        Assert.True(DateTime.TryParse(result.CopiedAt, out _));
        Assert.Equal(new StorageCopyClearCall("storage_copy_apply", _spec, "priya-to-staging", "clear"), result.Clear);
    }

    [Fact]
    public void CopiedBlobsCarryMetadataAndContentType()
    {
        StorageCopyWorld world = Populated().AcceptWrites();

        Apply(world, Plan(world).PlanHash);

        StubAzure.Recorded put = Assert.Single(world.Stub.Sent(HttpMethod.Put, "/staging-salon/drafts/A/s1.json"));
        Assert.Equal("Spring", put.Headers["x-ms-meta-title"]);
        Assert.Equal("application/json", put.Headers["x-ms-blob-content-type"]);
        Assert.Contains("src.blob.core.windows.net/test-salon-one/drafts/A/s1.json", put.Headers["x-ms-copy-source"], StringComparison.Ordinal);
        Assert.StartsWith("https://tgt.blob.core.windows.net/staging-salon/", put.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void CopiedEntitiesLandInTheTargetPartition()
    {
        StorageCopyWorld world = Populated().AcceptWrites();

        Apply(world, Plan(world).PlanHash);

        StubAzure.Recorded put = Assert.Single(world.Stub.Sent(HttpMethod.Put, "tgt.table."));
        Assert.Equal(Tgt, System.Text.Json.Nodes.JsonNode.Parse(put.Body!)!["PartitionKey"]!.GetValue<string>());
        Assert.Equal("k1", System.Text.Json.Nodes.JsonNode.Parse(put.Body!)!["RowKey"]!.GetValue<string>());
    }

    [Fact]
    public void ClearOnlyDeletes()
    {
        StorageCopyWorld world = Populated().AcceptWrites();

        StorageCopyApplyResult result = Apply(world, Plan(world, "clear").PlanHash, "clear");

        Assert.Equal("cleared", result.Outcome);
        Assert.Equal((1, 1, 0, 0), (result.DeletedBlobs, result.DeletedEntities, result.CopiedBlobs, result.CopiedEntities));
        Assert.DoesNotContain(Writes(world), w => w.Method == HttpMethod.Put);
        Assert.Null(result.Clear);
        Assert.Null(result.CopiedAt);
    }

    [Fact]
    public void AThrottledCopyIsRetried()
    {
        StorageCopyWorld world = Populated()
            .OnWrite(HttpMethod.Put, "/staging-salon/drafts/A/s1.json", StubAzure.Empty(429, ("Retry-After", "120")), StubAzure.Empty(201))
            .AcceptWrites();

        StorageCopyApplyResult result = Apply(world, Plan(world).PlanHash);

        Assert.Equal("applied", result.Outcome);
        Assert.Equal([TimeSpan.FromSeconds(30)], world.Stub.Slept);
        Assert.Equal(2, world.Stub.Sent(HttpMethod.Put, "/staging-salon/drafts/A/s1.json").Count());
    }

    [Fact]
    public void ThrottlingThreeTimesIsTheFailure()
    {
        StorageCopyWorld world = Populated()
            .OnWrite(HttpMethod.Put, "/staging-salon/drafts/A/s1.json", StubAzure.Empty(503))
            .AcceptWrites();

        StorageCopyApplyResult result = Apply(world, Plan(world).PlanHash);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal("copyBlob", result.Failure!.Operation);
        Assert.Equal("staging-salon/drafts/A/s1.json", result.Failure.Item);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], world.Stub.Slept);
        Assert.Equal(3, world.Stub.Requests.Count(r => r.Method == HttpMethod.Put));
        Assert.Equal(0, result.CopiedBlobs);
    }

    [Fact]
    public void TheFirstFailureStops()
    {
        StorageCopyWorld world = Populated()
            .OnWrite(HttpMethod.Put, "/staging-salon/images/logo.png", StubAzure.Xml(403, "<Error><Code>AuthorizationPermissionMismatch</Code><Message>no</Message></Error>", ("x-ms-error-code", "AuthorizationPermissionMismatch")))
            .AcceptWrites();

        StorageCopyApplyResult result = Apply(world, Plan(world).PlanHash);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal(1, result.CopiedBlobs);
        Assert.Equal(5, result.CopiedBytes);
        Assert.Equal("AuthorizationPermissionMismatch", result.Failure!.Error.Code);
        Assert.Equal("staging-salon/images/logo.png", result.Failure.Item);
        Assert.Equal(2, world.Stub.Requests.Count(r => r.Method == HttpMethod.Put));
        Assert.Empty(world.Stub.Sent(HttpMethod.Put, "tgt.table."));
        Assert.Empty(world.Stub.Slept);
        Assert.NotNull(result.CopiedAt);
    }

    [Fact]
    public void ADeleteThatFindsNothingIsNotAFailure()
    {
        StorageCopyWorld world = Populated()
            .OnWrite(HttpMethod.Delete, "tgt.blob.", StubAzure.Xml(404, "<Error><Code>BlobNotFound</Code><Message>gone</Message></Error>"))
            .AcceptWrites();

        StorageCopyApplyResult result = Apply(world, Plan(world).PlanHash);

        Assert.Equal("applied", result.Outcome);
        Assert.Equal(1, result.DeletedBlobs);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void ATokenThatFailsMidRunIsAFailureField()
    {
        StorageCopyWorld world = Populated().AcceptWrites();
        string hash = Plan(world).PlanHash;
        int before = world.Stub.Requests.Count;
        AzureHttp http = new(
            world.Stub,
            _ => world.Stub.Requests.Any(r => r.Method == HttpMethod.Delete)
                ? throw new GraphException("az token refresh failed")
                : StubAzure.Jwt(StubAzure.ObjectId, StubAzure.Upn),
            world.Stub.Slept.Add);

        StorageCopyApplyResult result = new StorageCopy(http).Apply(new StorageCopyRequest
        {
            Spec = _spec,
            Pair = "priya-to-staging",
            Mode = "refresh",
            Subscription = StubAzure.Sub,
            PlanHash = hash,
        });

        Assert.True(world.Stub.Requests.Count > before);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("token", result.Failure!.Error.Code);
        Assert.Equal("deleteEntity", result.Failure.Operation);
        Assert.Equal((1, 0, 0), (result.DeletedBlobs, result.DeletedEntities, result.CopiedBlobs));
        Assert.NotNull(result.Clear);
        Assert.Single(Writes(world));
    }

    [Fact]
    public void AnUnreachableTargetIsAFailureField()
    {
        StorageCopyWorld world = Populated()
            .OnWrite(HttpMethod.Put, "tgt.blob.", StubAzure.Throws(new HttpRequestException("no route to host")))
            .AcceptWrites();

        StorageCopyApplyResult result = Apply(world, Plan(world).PlanHash);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal("unreachable", result.Failure!.Error.Code);
        Assert.Contains("no route to host", result.Failure.Error.Message, StringComparison.Ordinal);
        Assert.Equal("copyBlob", result.Failure.Operation);
    }
}
