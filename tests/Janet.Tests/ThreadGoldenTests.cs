using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Runs each thread-item operation against a copy of the seed list and compares both halves
/// with what the original PowerShell produced: the file byte for byte, and the envelope it
/// printed.
/// </summary>
/// <remarks>
/// Both, because a port could match either alone and still be wrong. The file is the state
/// machine; the envelope is the contract callers read, and Show's is consumed by startup itself
/// under 'threadStack' in startup-manifest.json -- getting that shape wrong breaks a session's
/// first move.
///
/// The answers were recorded once by tests/Janet.Goldens from the scripts as they stood at
/// a277625, before the catalog shims landed. These tests need no PowerShell.
/// </remarks>
public class ThreadGoldenTests : IDisposable
{
    private readonly List<string> _directories = [];

    public static TheoryData<string> Labels() => [.. Cases.Threads.Select(t => t.Label)];

    [Theory]
    [MemberData(nameof(Labels))]
    public void TheListMatchesTheRecordedAnswer(string label)
    {
        string list = SeededCopy();

        Apply(label, list);

        GraphAssert.SameFile(Fixture.Golden("thread", label, ".json"), list, label);
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void TheEnvelopeMatchesTheRecordedAnswer(string label)
    {
        string list = SeededCopy();

        string actual = Apply(label, list);

        JsonObject expected = JsonNode.Parse(Fixture.ReadGolden("thread-output", label, ".json"))!.AsObject();
        JsonObject produced = JsonNode.Parse(actual)!.AsObject();

        foreach ((string field, JsonNode? value) in expected)
        {
            Assert.True(
                produced.ContainsKey(field),
                $"[{label}] envelope is missing '{field}', which callers of the PowerShell relied on");

            string[] grew = [.. AddedItemFields(value, produced[field]).Except(AddedToItems)];

            Assert.True(
                grew.Length == 0,
                $"[{label}] items in '{field}' grew undeclared field(s): {string.Join(", ", grew)}");

            Assert.True(
                JsonNode.DeepEquals(value, WithoutDeclaredAdditions(produced[field])),
                $"[{label}] '{field}' differs.\n  golden: {value?.ToJsonString()}\n  actual: {produced[field]?.ToJsonString()}");
        }

        // Anything else appearing here is drift, not a decision.
        string[] added = [.. produced.Select(p => p.Key).Where(k => !expected.ContainsKey(k))];

        Assert.True(
            added.Length == 0 || added.All(AddedToEnvelope.Contains),
            $"[{label}] envelope grew unexpected field(s): {string.Join(", ", added.Except(AddedToEnvelope))}");
    }

    /// <summary>
    /// The fields these envelopes are allowed to carry that the recorded answers do not.
    /// </summary>
    /// <remarks>
    /// THIS LIST IS THE DECLARED-CORRECTION MECHANISM, and it is edited by hand, in a diff a
    /// human reads. It is not regenerated, and neither are the goldens: tests/Janet.Goldens
    /// re-runs the PowerShell as it stood at a277625, so regenerating would only re-record the
    /// old shape -- a golden the implementation edits agrees with the implementation by
    /// construction and proves nothing. Everything not named here still fails, which is what
    /// keeps the correction a decision rather than a licence.
    ///
    /// 'batched' (envelope): how many requests shared the write. See ThreadJson.
    ///
    /// 'area' (item, 2026-09-03): the stored project label, added so that show and report can
    /// narrow to one project's items -- the list is shared by every repo on this machine. The
    /// envelope carries it resolved, so it reads '(unfiled)' for every item in these goldens:
    /// none were backfilled, deliberately, because inferring an area from a topic is the thing
    /// the field exists to avoid.
    /// </remarks>
    private static readonly string[] AddedToEnvelope = ["batched"];

    private static readonly string[] AddedToItems = ["area"];

    /// <summary>Keys the produced items carry that the recorded ones do not.</summary>
    private static string[] AddedItemFields(JsonNode? golden, JsonNode? produced)
    {
        if (golden is not JsonArray recorded || produced is not JsonArray actual)
        {
            return [];
        }

        HashSet<string> known =
        [
            .. recorded.OfType<JsonObject>().SelectMany(item => item.Select(field => field.Key))
        ];

        return
        [
            .. actual.OfType<JsonObject>()
                .SelectMany(item => item.Select(field => field.Key))
                .Where(key => !known.Contains(key))
                .Distinct(StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// The produced value with the declared item additions removed, so the rest is compared
    /// exactly as it always was.
    /// </summary>
    private static JsonNode? WithoutDeclaredAdditions(JsonNode? produced)
    {
        if (produced is not JsonArray actual)
        {
            return produced;
        }

        JsonArray stripped = [];

        foreach (JsonNode? element in actual)
        {
            if (element is not JsonObject item)
            {
                stripped.Add(element?.DeepClone());
                continue;
            }

            JsonObject copy = item.DeepClone().AsObject();

            foreach (string field in AddedToItems)
            {
                copy.Remove(field);
            }

            stripped.Add(copy);
        }

        return stripped;
    }

    /// <summary>
    /// The same operations as <see cref="Cases.Threads"/>, expressed as Janet.Core calls.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than bound from the argument list: driving both sides through one
    /// translator would let a bug in the translator cancel itself out. If this drifts from the
    /// case list, the golden stops describing what ran and the test fails, which is the point.
    /// </remarks>
    private static string Apply(string label, string list) => label switch
    {
        "thread add" =>
            ThreadJson.Serialize(ThreadItems.Add(list, "something noticed in passing")),

        "thread add active" =>
            ThreadJson.Serialize(ThreadItems.Add(list, "urgent detour", active: true)),

        "thread add furnished" =>
            ThreadJson.Serialize(ThreadItems.Add(
                list,
                "a topic with a comma, an apostrophe (') and a \"quote\"",
                notes: "Notes that run on, with punctuation: A -> B & C.",
                next: "do the next thing",
                refs: ["note.one", "note.two"])),

        "thread update notes" =>
            ThreadJson.Serialize(ThreadItems.Update(
                list, Topic("cache eviction"), notes: "replaced wholesale")),

        "thread update appending notes" =>
            ThreadJson.Serialize(ThreadItems.Update(
                list, Topic("cache eviction"), notes: "and then this", appendNotes: true)),

        "thread update appending to empty notes" =>
            ThreadJson.Serialize(ThreadItems.Update(
                list, Topic("cache warming"), notes: "first note", appendNotes: true)),

        "thread update appending refs" =>
            ThreadJson.Serialize(ThreadItems.Update(
                list, Topic("cache eviction"), refs: ["note.cache", "note.new"], appendRefs: true)),

        "thread update clearing next" =>
            ThreadJson.Serialize(ThreadItems.Update(list, Topic("cache eviction"), next: "")),

        "thread update status parks the other" =>
            ThreadJson.Serialize(ThreadItems.Update(
                list, Topic("cache eviction"), status: ThreadItems.Active)),

        "thread complete by topic" =>
            ThreadJson.Serialize(ThreadItems.Complete(list, Topic("cache eviction"))),

        "thread complete the active one" =>
            ThreadJson.Serialize(ThreadItems.Complete(list, new ThreadSelector())),

        "thread set active" =>
            ThreadJson.Serialize(ThreadItems.SetActive(list, Topic("cache eviction"))),

        "thread clear active" =>
            ThreadJson.Serialize(ThreadItems.SetActive(list, null)),

        "thread show" =>
            ThreadJson.Serialize(ThreadItems.Show(list)),

        "thread show all" =>
            ThreadJson.Serialize(ThreadItems.Show(list, all: true)),

        _ => throw new ArgumentException($"no Janet.Core equivalent for case '{label}'"),
    };

    private static ThreadSelector Topic(string topic) => new() { Topic = topic };

    private string SeededCopy()
    {
        string directory = Path.Combine(Path.GetTempPath(), "janet-thread-golden", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        string list = Path.Combine(directory, "thread-stack.json");
        File.Copy(Fixture.Resolve("Fixtures", "threads.json"), list);

        return list;
    }

    public void Dispose()
    {
        foreach (string directory in _directories.Where(Directory.Exists))
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
        }

        GC.SuppressFinalize(this);
    }
}
