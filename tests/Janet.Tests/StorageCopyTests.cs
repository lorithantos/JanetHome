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
}
