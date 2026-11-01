using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>What to plan (and, for apply, to do).</summary>
public sealed class StorageCopyRequest
{
    /// <summary>Absolute path of the storage copy spec.</summary>
    public required string Spec { get; init; }

    public required string Pair { get; init; }

    /// <summary>"refresh" or "clear"; anything else throws.</summary>
    public required string Mode { get; init; }

    /// <summary>A blob rule's match text, or a table name: list the names for that rule only.</summary>
    public string? Rule { get; init; }

    public string? Subscription { get; init; }

    /// <summary>Holds BOTH accounts; omit when they differ.</summary>
    public string? ResourceGroup { get; init; }

    /// <summary>Apply only.</summary>
    public string? PlanHash { get; init; }
}

public sealed record RuleCount(string Rule, int Count);

public sealed record RuleCopy(string Rule, int Count, long Bytes);

public sealed record ReasonCount(string Reason, int Count);

public sealed record StorageCopyNames(IReadOnlyList<string> Delete, IReadOnlyList<string> Copy);

public sealed record StorageCopyPlanResult
{
    /// <summary>Format version. See contracts\storage-copy-plan.schema.json.</summary>
    public const int ContractVersion = 1;

    public int Contract => ContractVersion;

    public required string Mode { get; init; }

    public required string Pair { get; init; }

    public required string Spec { get; init; }

    public required StorageCopyEnd Source { get; init; }

    public required StorageCopyEnd Target { get; init; }

    public required int DeleteBlobs { get; init; }

    public required int DeleteEntities { get; init; }

    public required IReadOnlyList<RuleCount> DeleteByRule { get; init; }

    public required int CopyBlobs { get; init; }

    public required long CopyBytes { get; init; }

    public required int CopyEntities { get; init; }

    public required IReadOnlyList<RuleCopy> CopyByRule { get; init; }

    public required int DroppedCount { get; init; }

    public required IReadOnlyList<ReasonCount> DroppedByReason { get; init; }

    public required int UnmatchedCount { get; init; }

    /// <summary>The first 100, sorted.</summary>
    public required IReadOnlyList<string> UnmatchedNames { get; init; }

    /// <summary>Null unless the request named a rule.</summary>
    public required StorageCopyNames? Names { get; init; }

    public required bool ReadyToApply { get; init; }

    public required IReadOnlyList<string> Blockers { get; init; }

    public required string PlanHash { get; init; }
}

internal enum StorageCopyOpKind
{
    DeleteBlob,
    DeleteEntity,
    CopyBlob,
    CopyEntity,
}

/// <summary>
/// One thing the plan would do, carrying everything needed to do it without re-deriving anything.
/// Container is the container or table on the side the operation WRITES to (target).
/// </summary>
/// <param name="Name">A blob's target name, or "partitionKey/rowKey" for an entity.</param>
/// <param name="ETag">The ETag of the item read: the target's for a delete, the source's for a copy.</param>
internal sealed record StorageCopyOp(StorageCopyOpKind Kind, string Rule, string Container, string Name, string ETag)
{
    /// <summary>Copy blob: the source blob's URL on the SOURCE blob endpoint, escaped by <see cref="StorageDataPlane.BlobUrl"/>.</summary>
    public string? SourceUrl { get; init; }

    public string? ContentType { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Copy blob: the source length in bytes.</summary>
    public long Length { get; init; }

    /// <summary>Entity operations: the TARGET partition key (for a delete, the key of the row to delete).</summary>
    public string? PartitionKey { get; init; }

    public string? RowKey { get; init; }

    /// <summary>Copy entity: the SOURCE entity body as read. Send <see cref="StorageDataPlane.ForTarget"/> of it with <see cref="PartitionKey"/>.</summary>
    public JsonObject? Entity { get; init; }

    internal string HashLine()
    {
        string kind = Kind switch
        {
            StorageCopyOpKind.DeleteBlob => "deleteBlob",
            StorageCopyOpKind.DeleteEntity => "deleteEntity",
            StorageCopyOpKind.CopyBlob => "copyBlob",
            _ => "copyEntity",
        };

        return $"{kind}\t{Container}\t{Name}\t{ETag}";
    }
}

/// <summary>
/// Everything a plan found: the public result, plus the ordered operation list the hash covers and the
/// endpoints those operations go to. Apply replays <see cref="Operations"/> exactly as hashed.
/// </summary>
/// <param name="Operations">Delete blobs, delete entities, copy blobs, copy entities; each sorted ordinally by container then name.</param>
internal sealed record StorageCopyInventory(
    StorageCopyPlanResult Result,
    IReadOnlyList<StorageCopyOp> Operations,
    string SourceBlobEndpoint,
    string TargetBlobEndpoint,
    string TargetTableEndpoint);

/// <summary>
/// Plans a refresh (or clear) of a temporary staging copy of one client's live storage data: what
/// would be deleted from the target, what would be copied from the source, what is dropped as dead,
/// and what blocks. Read-only.
/// </summary>
/// <remarks>
/// PLAN AND APPLY SHARE ONE INVENTORY. The hash covers the operation list the inventory produced, and
/// apply recomputes the inventory and replays that same list, so what was approved is what runs.
/// </remarks>
public sealed class StorageCopy(AzureHttp http)
{
    private const int UnmatchedNameLimit = 100;

    private static readonly IReadOnlyDictionary<string, string> NoCaptures = new Dictionary<string, string>();

    private readonly StorageDataPlane _plane = new(http);

    public static StorageCopy Shared { get; } = new(AzureHttp.Shared);

    public StorageCopyPlanResult Plan(StorageCopyRequest request) => Inventory(request).Result;

    internal StorageCopyInventory Inventory(StorageCopyRequest request)
    {
        bool refresh = request.Mode switch
        {
            "refresh" => true,
            "clear" => false,
            _ => throw new GraphException($"Unknown mode '{request.Mode}'. Use 'refresh' or 'clear'."),
        };

        StorageCopySpec spec = StorageCopySpec.Load(request.Spec);
        StorageCopyPair pair = spec.Guard(request.Pair);
        string? rule = request.Rule;
        if (rule is not null)
        {
            CheckRule(spec, rule);
        }

        StorageCopyEnd source = pair.Source;
        StorageCopyEnd target = pair.Target;
        AzureSubscription subscription = AzureSubscription.Resolve(request.Subscription);
        (StorageAccountSettings? sourceAccount, StorageAccountSettings targetAccount) =
            FindAccounts(source.Account, target.Account, request.ResourceGroup, subscription, refresh);

        List<StorageCopyOp> deleteBlobs = [];
        List<StorageCopyOp> deleteEntities = [];
        List<StorageCopyOp> copyBlobs = [];
        List<StorageCopyOp> copyEntities = [];
        Dictionary<string, int> dropped = new(StringComparer.Ordinal);
        List<string> unmatched = [];
        List<string> blockers = [];
        Dictionary<string, SourceBlobs> sourceByContainerSpec = new(StringComparer.Ordinal);

        foreach (ContainerSpec container in spec.Containers)
        {
            string sourceContainer = container.Name.Expand(NoCaptures, source.Client);
            string targetContainer = container.Name.Expand(NoCaptures, target.Client);
            string? sourcePrefix = container.Prefix?.Expand(NoCaptures, source.Client);
            string? targetPrefix = container.Prefix?.Expand(NoCaptures, target.Client);

            IReadOnlyList<BlobItem>? targetBlobs = _plane.ListBlobs(targetAccount.BlobEndpoint, targetContainer, targetPrefix);
            if (targetBlobs is null)
            {
                blockers.Add($"The target container '{targetContainer}' in account '{target.Account}' does not exist; Janet never creates it.");
            }
            else
            {
                foreach (BlobItem blob in InScope(targetBlobs, targetPrefix))
                {
                    BlobRule? matched = container.Rules.FirstOrDefault(r => r.Match.Match(blob.Name, target.Client) is not null);
                    if (matched is { Copy: true })
                    {
                        deleteBlobs.Add(new StorageCopyOp(StorageCopyOpKind.DeleteBlob, matched.Match.Text, targetContainer, blob.Name, blob.ETag));
                    }
                }
            }

            if (!refresh)
            {
                continue;
            }

            IReadOnlyList<BlobItem>? sourceBlobs = _plane.ListBlobs(sourceAccount!.BlobEndpoint, sourceContainer, sourcePrefix);
            if (sourceBlobs is null)
            {
                blockers.Add($"The source container '{sourceContainer}' in account '{source.Account}' does not exist.");
                sourceByContainerSpec[container.Name.Text] = new SourceBlobs(container, source.Client, []);
                continue;
            }

            SourceBlobs sourceSide = new(container, source.Client, InScope(sourceBlobs, sourcePrefix));
            sourceByContainerSpec[container.Name.Text] = sourceSide;

            foreach (BlobItem blob in sourceSide.Blobs)
            {
                Verdict verdict = sourceSide.Judge(blob);
                switch (verdict.Kind)
                {
                    case VerdictKind.Unmatched:
                        unmatched.Add(blob.Name);
                        break;
                    case VerdictKind.Dropped:
                        dropped[verdict.Reason!] = dropped.GetValueOrDefault(verdict.Reason!) + 1;
                        break;
                    case VerdictKind.Keep:
                        string targetName = verdict.Rule!.Match.Expand(verdict.Captures!, target.Client);
                        copyBlobs.Add(new StorageCopyOp(StorageCopyOpKind.CopyBlob, verdict.Rule.Match.Text, targetContainer, targetName, blob.ETag)
                        {
                            SourceUrl = StorageDataPlane.BlobUrl(sourceAccount.BlobEndpoint, sourceContainer, blob.Name),
                            ContentType = blob.ContentType,
                            Metadata = blob.Metadata,
                            Length = blob.Length,
                        });
                        break;
                }
            }
        }

        foreach (TableSpec table in spec.Tables)
        {
            string targetPartition = table.PartitionKey.Expand(NoCaptures, target.Client);
            IReadOnlyList<EntityItem>? targetEntities = _plane.QueryPartition(targetAccount.TableEndpoint, table.Name, targetPartition);
            if (targetEntities is null)
            {
                blockers.Add($"The target table '{table.Name}' in account '{target.Account}' does not exist; Janet never creates it.");
            }
            else
            {
                foreach (EntityItem entity in targetEntities)
                {
                    deleteEntities.Add(EntityOp(StorageCopyOpKind.DeleteEntity, table.Name, table.Name, entity.PartitionKey, entity.RowKey, entity.ETag));
                }
            }

            if (!refresh)
            {
                continue;
            }

            string sourcePartition = table.PartitionKey.Expand(NoCaptures, source.Client);
            IReadOnlyList<EntityItem>? sourceEntities = _plane.QueryPartition(sourceAccount!.TableEndpoint, table.Name, sourcePartition);
            if (sourceEntities is null)
            {
                blockers.Add($"The source table '{table.Name}' in account '{source.Account}' does not exist.");
                continue;
            }

            foreach (EntityItem entity in sourceEntities)
            {
                if (table.KeepIfLive is not null && !ParentIsLive(table, entity, source.Client, sourceByContainerSpec))
                {
                    dropped["noLiveSave"] = dropped.GetValueOrDefault("noLiveSave") + 1;
                    continue;
                }

                StorageCopyOp op = EntityOp(StorageCopyOpKind.CopyEntity, table.Name, table.Name, targetPartition, entity.RowKey, entity.ETag);
                copyEntities.Add(op with { Entity = entity.Body });
            }
        }

        if (unmatched.Count > 0)
        {
            blockers.Insert(0, $"{unmatched.Count} source blob(s) in scope match no rule; see unmatched.names, then add a rule or \"copy\": false.");
        }

        List<StorageCopyOp> operations = [.. Sorted(deleteBlobs), .. Sorted(deleteEntities), .. Sorted(copyBlobs), .. Sorted(copyEntities)];
        string hash = Hash(request.Mode, request.Pair, source, target, operations);
        List<string> sortedUnmatched = [.. unmatched.Order(StringComparer.Ordinal)];

        StorageCopyPlanResult result = new()
        {
            Mode = request.Mode,
            Pair = pair.Name,
            Spec = request.Spec,
            Source = source,
            Target = target,
            DeleteBlobs = deleteBlobs.Count,
            DeleteEntities = deleteEntities.Count,
            DeleteByRule = [.. deleteBlobs.Concat(deleteEntities).GroupBy(o => o.Rule).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new RuleCount(g.Key, g.Count()))],
            CopyBlobs = copyBlobs.Count,
            CopyBytes = copyBlobs.Sum(o => o.Length),
            CopyEntities = copyEntities.Count,
            CopyByRule = [.. copyBlobs.Concat(copyEntities).GroupBy(o => o.Rule).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new RuleCopy(g.Key, g.Count(), g.Sum(o => o.Length)))],
            DroppedCount = dropped.Values.Sum(),
            DroppedByReason = [.. dropped.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => new ReasonCount(d.Key, d.Value))],
            UnmatchedCount = unmatched.Count,
            UnmatchedNames = [.. sortedUnmatched.Take(UnmatchedNameLimit)],
            Names = rule is null ? null : new StorageCopyNames(NamesFor(operations, rule, deletes: true), NamesFor(operations, rule, deletes: false)),
            ReadyToApply = blockers.Count == 0,
            Blockers = blockers,
            PlanHash = hash,
        };

        return new StorageCopyInventory(result, operations, sourceAccount?.BlobEndpoint ?? string.Empty, targetAccount.BlobEndpoint, targetAccount.TableEndpoint);
    }

    private static StorageCopyOp EntityOp(StorageCopyOpKind kind, string rule, string table, string partitionKey, string rowKey, string etag) =>
        new(kind, rule, table, partitionKey + "/" + rowKey, etag) { PartitionKey = partitionKey, RowKey = rowKey };

    private static IEnumerable<BlobItem> InScope(IReadOnlyList<BlobItem> blobs, string? prefix) =>
        prefix is null ? blobs : blobs.Where(b => b.Name.StartsWith(prefix, StringComparison.Ordinal));

    private static IEnumerable<StorageCopyOp> Sorted(List<StorageCopyOp> ops) =>
        ops.OrderBy(o => o.Container, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal);

    private static List<string> NamesFor(List<StorageCopyOp> operations, string rule, bool deletes) =>
    [
        .. operations
            .Where(o => o.Rule == rule && (o.Kind is StorageCopyOpKind.DeleteBlob or StorageCopyOpKind.DeleteEntity) == deletes)
            .Select(o => o.Name)
            .Order(StringComparer.Ordinal),
    ];

    private static void CheckRule(StorageCopySpec spec, string rule)
    {
        List<string> names = [.. spec.Containers.SelectMany(c => c.Rules).Select(r => r.Match.Text).Concat(spec.Tables.Select(t => t.Name))];
        if (!names.Contains(rule))
        {
            throw new GraphException($"No rule or table named '{rule}'. Names: {string.Join(", ", names.Select(n => $"'{n}'"))}.");
        }
    }

    private static string Hash(string mode, string pair, StorageCopyEnd source, StorageCopyEnd target, IEnumerable<StorageCopyOp> operations)
    {
        IEnumerable<string> lines =
        [
            $"mode\t{mode}",
            $"pair\t{pair}",
            $"source\t{source.Account}\t{source.Client}",
            $"target\t{target.Account}\t{target.Client}",
            .. operations.Select(o => o.HashLine()),
        ];

        string joined = string.Join("\n", lines.Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    private static bool ParentIsLive(TableSpec table, EntityItem entity, string sourceClient, Dictionary<string, SourceBlobs> sourceByContainerSpec)
    {
        TableKeepIfLive keep = table.KeepIfLive!;
        Dictionary<string, string> captures = [];
        if (table.RowKey is not null)
        {
            IReadOnlyDictionary<string, string>? row = table.RowKey.Match(entity.RowKey, sourceClient);
            if (row is null)
            {
                return false;
            }

            foreach ((string key, string value) in row)
            {
                captures[key] = value;
            }
        }

        return sourceByContainerSpec.TryGetValue(keep.Container.Text, out SourceBlobs? side)
            && side.IsLive(keep.Blob.Expand(captures, sourceClient));
    }

    /// <summary>The source is null when <paramref name="needSource"/> is false: clear never reads it.</summary>
    private (StorageAccountSettings? Source, StorageAccountSettings Target) FindAccounts(
        string sourceAccount,
        string targetAccount,
        string? resourceGroup,
        AzureSubscription subscription,
        bool needSource)
    {
        if (!string.IsNullOrWhiteSpace(resourceGroup))
        {
            return (
                needSource ? StorageProbe.Settings(StorageProbe.FindAccount(http, sourceAccount, resourceGroup, subscription)) : null,
                StorageProbe.Settings(StorageProbe.FindAccount(http, targetAccount, resourceGroup, subscription)));
        }

        IReadOnlyList<JsonObject> all = http.ArmList(
            $"/subscriptions/{subscription.Id}/providers/Microsoft.Storage/storageAccounts?api-version={AzureHttp.StorageApi}",
            "list storage accounts");

        StorageAccountSettings Pick(string name) => StorageProbe.Settings(
            all.FirstOrDefault(a => string.Equals(a["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
            ?? throw new GraphException($"No storage account '{name}' in subscription {subscription.Id} ({subscription.Name ?? subscription.Source})."));

        return (needSource ? Pick(sourceAccount) : null, Pick(targetAccount));
    }

    private enum VerdictKind
    {
        Keep,
        Dropped,
        Excluded,
        Unmatched,
    }

    private sealed record Verdict(VerdictKind Kind, BlobRule? Rule = null, IReadOnlyDictionary<string, string>? Captures = null, string? Reason = null);

    /// <summary>One container's in-scope source blobs, and the rule-by-rule verdict on each.</summary>
    private sealed class SourceBlobs
    {
        private readonly ContainerSpec _container;
        private readonly string _client;
        private readonly Dictionary<string, BlobItem> _byName;
        private readonly Dictionary<string, Verdict> _verdicts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _judging = new(StringComparer.Ordinal);

        public SourceBlobs(ContainerSpec container, string client, IEnumerable<BlobItem> blobs)
        {
            _container = container;
            _client = client;
            Blobs = [.. blobs];
            _byName = Blobs.ToDictionary(b => b.Name, StringComparer.Ordinal);
        }

        public IReadOnlyList<BlobItem> Blobs { get; }

        /// <summary>The named blob exists here and is itself kept under its own rule.</summary>
        public bool IsLive(string name) => _byName.TryGetValue(name, out BlobItem? blob) && Judge(blob).Kind == VerdictKind.Keep;

        public Verdict Judge(BlobItem blob)
        {
            if (_verdicts.TryGetValue(blob.Name, out Verdict? known))
            {
                return known;
            }

            // A keepIfLive chain that loops back on itself has no live end.
            if (!_judging.Add(blob.Name))
            {
                return new Verdict(VerdictKind.Dropped, Reason: "noLiveSave");
            }

            Verdict verdict = Decide(blob);
            _judging.Remove(blob.Name);
            _verdicts[blob.Name] = verdict;
            return verdict;
        }

        private Verdict Decide(BlobItem blob)
        {
            foreach (BlobRule rule in _container.Rules)
            {
                IReadOnlyDictionary<string, string>? captures = rule.Match.Match(blob.Name, _client);
                if (captures is null)
                {
                    continue;
                }

                if (!rule.Copy)
                {
                    return new Verdict(VerdictKind.Excluded, rule, captures);
                }

                if (rule.DropIfMetadata is not null && blob.Metadata.ContainsKey(rule.DropIfMetadata))
                {
                    return new Verdict(VerdictKind.Dropped, rule, captures, rule.DropIfMetadata);
                }

                if (rule.KeepIfLive is not null && !IsLive(rule.KeepIfLive.Expand(captures, _client)))
                {
                    return new Verdict(VerdictKind.Dropped, rule, captures, "noLiveSave");
                }

                return new Verdict(VerdictKind.Keep, rule, captures);
            }

            return new Verdict(VerdictKind.Unmatched);
        }
    }
}
