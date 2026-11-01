using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>One side of a copy: a storage account and the client whose data lives there.</summary>
public sealed record StorageCopyEnd(string Account, string Client);

public sealed record StorageCopyPair(string Name, StorageCopyEnd Source, StorageCopyEnd Target);

/// <param name="Match">Blob path pattern; rules are tried in order, first match wins.</param>
/// <param name="DropIfMetadata">Drop a blob whose metadata carries this key.</param>
/// <param name="KeepIfLive">Keep a blob only if the named blob exists at the source and is itself live.</param>
/// <param name="Copy">False excludes matching blobs deliberately: neither copied nor wiped.</param>
public sealed record BlobRule(StoragePattern Match, string? DropIfMetadata, StoragePattern? KeepIfLive, bool Copy);

public sealed record ContainerSpec(StoragePattern Name, StoragePattern? Prefix, IReadOnlyList<BlobRule> Rules);

public sealed record TableKeepIfLive(StoragePattern Container, StoragePattern Blob);

public sealed record TableSpec(string Name, StoragePattern PartitionKey, StoragePattern? RowKey, TableKeepIfLive? KeepIfLive);

/// <summary>
/// The storage copy spec: which accounts and clients pair up, and which blobs and rows travel.
/// Parsed strictly: an unknown key, a placeholder a keepIfLive cannot resolve, a keepIfLive no rule
/// covers, or a repeated pair name refuses the whole spec.
/// </summary>
public sealed class StorageCopySpec
{
    private static readonly string[] RootKeys = ["pairs", "containers", "tables"];
    private static readonly string[] PairKeys = ["name", "source", "target"];
    private static readonly string[] EndKeys = ["account", "client"];
    private static readonly string[] ContainerKeys = ["name", "prefix", "rules"];
    private static readonly string[] RuleKeys = ["match", "dropIfMetadata", "keepIfLive", "copy"];
    private static readonly string[] TableKeys = ["name", "partitionKey", "rowKey", "keepIfLive"];
    private static readonly string[] TableKeepKeys = ["container", "blob"];

    // A value no real name uses, standing in for each placeholder when asking whether a rule covers a pattern.
    private const string SampleValue = "x";
    private const string SampleClient = "sample-client";

    private readonly string _source;

    private StorageCopySpec(
        string source,
        IReadOnlyList<StorageCopyPair> pairs,
        IReadOnlyList<ContainerSpec> containers,
        IReadOnlyList<TableSpec> tables)
    {
        _source = source;
        Pairs = pairs;
        Containers = containers;
        Tables = tables;
    }

    public IReadOnlyList<StorageCopyPair> Pairs { get; }

    public IReadOnlyList<ContainerSpec> Containers { get; }

    public IReadOnlyList<TableSpec> Tables { get; }

    /// <summary>Reads and parses the spec at an absolute path.</summary>
    public static StorageCopySpec Load(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new GraphException($"The storage copy spec path must be absolute: '{path}'.");
        }

        if (!File.Exists(path))
        {
            throw new GraphException($"No storage copy spec at '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    /// <param name="source">Names the file in messages.</param>
    public static StorageCopySpec Parse(string json, string source)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new GraphException($"{source}: not valid JSON: {ex.Message}");
        }

        var obj = AsObject(root, "$", source, RootKeys);
        var pairs = Items(obj, "pairs", source).Select((n, i) => ParsePair(n, $"pairs[{i}]", source)).ToList();
        var containers = Items(obj, "containers", source).Select((n, i) => ParseContainer(n, $"containers[{i}]", source)).ToList();
        var tables = Items(obj, "tables", source).Select((n, i) => ParseTable(n, $"tables[{i}]", source)).ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Count; i++)
        {
            if (!seen.Add(pairs[i].Name))
            {
                throw new GraphException($"{source}: pairs[{i}] repeats the pair name '{pairs[i].Name}'.");
            }
        }

        for (var i = 0; i < tables.Count; i++)
        {
            CheckTableKeepIfLive(tables[i], containers, $"tables[{i}]", source);
        }

        return new StorageCopySpec(source, pairs, containers, tables);
    }

    /// <summary>
    /// Spec section 5. Guard 1: the pair exists. Guard 2: its target is listed as a target. Guard 4: source and
    /// target are not the same account and client. Guard 3: no account is a source in any pair and a target in any
    /// pair. Guard 4 is checked before guard 3 because it is the special case of it: a pair whose source and target
    /// are the same account trips guard 3 too, and the message that says "same" is the useful one.
    /// </summary>
    public StorageCopyPair Guard(string pairName)
    {
        var pair = Pairs.FirstOrDefault(p => p.Name == pairName)
            ?? throw new GraphException(
                $"{_source}: no pair named '{pairName}'. Pairs: {string.Join(", ", Pairs.Select(p => $"'{p.Name}'"))}.");

        if (!Pairs.Any(p => p.Target.Account == pair.Target.Account))
        {
            throw new GraphException($"{_source}: account '{pair.Target.Account}' is not listed as a target.");
        }

        // Azure resolves account names case-insensitively, so "Acct" and "acct" are the same account.
        if (string.Equals(pair.Source.Account, pair.Target.Account, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.Source.Client, pair.Target.Client, StringComparison.OrdinalIgnoreCase))
        {
            throw new GraphException(
                $"{_source}: pair '{pair.Name}' has the same account '{pair.Source.Account}' and client '{pair.Source.Client}' as source and target.");
        }

        foreach (var asSource in Pairs)
        {
            var asTarget = Pairs.FirstOrDefault(
                p => string.Equals(p.Target.Account, asSource.Source.Account, StringComparison.OrdinalIgnoreCase));
            if (asTarget is not null)
            {
                throw new GraphException(
                    $"{_source}: account '{asSource.Source.Account}' is the source of pair '{asSource.Name}' "
                    + $"and the target of pair '{asTarget.Name}'. A source account can never be written.");
            }
        }

        return pair;
    }

    private static StorageCopyPair ParsePair(JsonNode? node, string path, string source)
    {
        var o = AsObject(node, path, source, PairKeys);
        return new StorageCopyPair(
            RequiredString(o, "name", path, source),
            ParseEnd(Required(o, "source", path, source), $"{path}.source", source),
            ParseEnd(Required(o, "target", path, source), $"{path}.target", source));
    }

    private static StorageCopyEnd ParseEnd(JsonNode? node, string path, string source)
    {
        var o = AsObject(node, path, source, EndKeys);
        return new StorageCopyEnd(
            RequiredString(o, "account", path, source),
            RequiredString(o, "client", path, source));
    }

    private static ContainerSpec ParseContainer(JsonNode? node, string path, string source)
    {
        var o = AsObject(node, path, source, ContainerKeys);
        var name = ParsePattern(RequiredString(o, "name", path, source), $"{path}.name", source);
        var prefixText = OptionalString(o, "prefix", path, source);
        var prefix = prefixText is null ? null : ParsePattern(prefixText, $"{path}.prefix", source);
        var rules = Items(o, "rules", source, path)
            .Select((n, i) => ParseRule(n, $"{path}.rules[{i}]", source))
            .ToList();

        for (var i = 0; i < rules.Count; i++)
        {
            var keep = rules[i].KeepIfLive;
            if (keep is null)
            {
                continue;
            }

            var rulePath = $"{path}.rules[{i}].keepIfLive";
            RequireCaptured(keep, rules[i].Match.Placeholders, rulePath, "its match", source);
            if (!Covered(keep, rules))
            {
                throw new GraphException($"{source}: {rulePath} names '{keep.Text}', which no rule in {path} matches.");
            }
        }

        return new ContainerSpec(name, prefix, rules);
    }

    private static BlobRule ParseRule(JsonNode? node, string path, string source)
    {
        var o = AsObject(node, path, source, RuleKeys);
        var match = ParsePattern(RequiredString(o, "match", path, source), $"{path}.match", source);
        var keepText = OptionalString(o, "keepIfLive", path, source);
        var keep = keepText is null ? null : ParsePattern(keepText, $"{path}.keepIfLive", source);
        var copy = true;
        if (o.TryGetPropertyValue("copy", out var c))
        {
            if (c is not JsonValue cv || !cv.TryGetValue<bool>(out copy))
            {
                throw new GraphException($"{source}: {path}.copy must be true or false.");
            }
        }

        return new BlobRule(match, OptionalString(o, "dropIfMetadata", path, source), keep, copy);
    }

    private static TableSpec ParseTable(JsonNode? node, string path, string source)
    {
        var o = AsObject(node, path, source, TableKeys);
        var partition = ParsePattern(RequiredString(o, "partitionKey", path, source), $"{path}.partitionKey", source);
        var rowText = OptionalString(o, "rowKey", path, source);
        var row = rowText is null ? null : ParsePattern(rowText, $"{path}.rowKey", source);

        TableKeepIfLive? keep = null;
        if (o.TryGetPropertyValue("keepIfLive", out var k))
        {
            var ko = AsObject(k, $"{path}.keepIfLive", source, TableKeepKeys);
            keep = new TableKeepIfLive(
                ParsePattern(RequiredString(ko, "container", $"{path}.keepIfLive", source), $"{path}.keepIfLive.container", source),
                ParsePattern(RequiredString(ko, "blob", $"{path}.keepIfLive", source), $"{path}.keepIfLive.blob", source));
        }

        return new TableSpec(RequiredString(o, "name", path, source), partition, row, keep);
    }

    private static void CheckTableKeepIfLive(TableSpec table, List<ContainerSpec> containers, string path, string source)
    {
        var keep = table.KeepIfLive;
        if (keep is null)
        {
            return;
        }

        var captured = table.PartitionKey.Placeholders.Concat(table.RowKey?.Placeholders ?? []).ToList();
        RequireCaptured(keep.Blob, captured, $"{path}.keepIfLive.blob", "its partitionKey and rowKey", source);
        RequireCaptured(keep.Container, captured, $"{path}.keepIfLive.container", "its partitionKey and rowKey", source);

        var container = containers.FirstOrDefault(c => c.Name.Text == keep.Container.Text);
        if (container is null)
        {
            throw new GraphException($"{source}: {path}.keepIfLive.container '{keep.Container.Text}' names no container in the spec.");
        }

        if (!Covered(keep.Blob, container.Rules))
        {
            throw new GraphException(
                $"{source}: {path}.keepIfLive.blob names '{keep.Blob.Text}', which no rule in container '{container.Name.Text}' matches.");
        }
    }

    private static void RequireCaptured(StoragePattern uses, IReadOnlyList<string> captured, string path, string from, string source)
    {
        foreach (var name in uses.Placeholders)
        {
            if (name != StoragePattern.ClientPlaceholder && !captured.Contains(name))
            {
                throw new GraphException($"{source}: {path} uses {{{name}}}, which {from} does not capture.");
            }
        }
    }

    // Whether any rule matches the pattern when every placeholder is filled with a sample segment.
    private static bool Covered(StoragePattern pattern, IReadOnlyList<BlobRule> rules)
    {
        var captures = pattern.Placeholders
            .Where(n => n != StoragePattern.ClientPlaceholder)
            .ToDictionary(n => n, _ => SampleValue);
        var sample = pattern.Expand(captures, SampleClient);
        return rules.Any(r => r.Match.Match(sample, SampleClient) is not null);
    }

    private static StoragePattern ParsePattern(string text, string path, string source)
    {
        try
        {
            return StoragePattern.Parse(text);
        }
        catch (GraphException ex)
        {
            throw new GraphException($"{source}: {path}: {ex.Message}");
        }
    }

    private static JsonObject AsObject(JsonNode? node, string path, string source, string[] allowed)
    {
        if (node is not JsonObject o)
        {
            throw new GraphException($"{source}: {path} must be an object.");
        }

        foreach (var (key, _) in o)
        {
            if (!allowed.Contains(key))
            {
                throw new GraphException(
                    $"{source}: {path} has an unknown key '{key}'. Allowed: {string.Join(", ", allowed)}.");
            }
        }

        return o;
    }

    private static IEnumerable<JsonNode?> Items(JsonObject o, string key, string source, string parent = "$")
    {
        var where = parent == "$" ? key : $"{parent}.{key}";
        if (!o.TryGetPropertyValue(key, out var n) || n is not JsonArray a)
        {
            throw new GraphException($"{source}: {where} must be an array.");
        }

        return a;
    }

    private static JsonNode Required(JsonObject o, string key, string path, string source) =>
        o.TryGetPropertyValue(key, out var n) && n is not null
            ? n
            : throw new GraphException($"{source}: {path} is missing '{key}'.");

    private static string RequiredString(JsonObject o, string key, string path, string source) =>
        OptionalString(o, key, path, source)
        ?? throw new GraphException($"{source}: {path} is missing '{key}'.");

    private static string? OptionalString(JsonObject o, string key, string path, string source)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is null)
        {
            return null;
        }

        if (n is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length == 0)
        {
            throw new GraphException($"{source}: {path}.{key} must be a non-empty string.");
        }

        return s;
    }
}
