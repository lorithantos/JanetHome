using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The reporter: the list as a map, with the note bodies left on disk.
/// </summary>
/// <remarks>
/// It exists because Show answers a more expensive question than resuming needs. On 2026-08-14
/// the live list was 174,129 characters through Show -- past what an MCP tool result will carry,
/// so thread_show refused outright -- against 26,613 through the reporter, with three items
/// holding a third of the total between them.
///
/// The behaviour worth guarding is not the size but the HONESTY: an envelope that dropped the
/// notes silently would look complete and read as "these items have no notes". Every test here
/// is ultimately about notesLength being present and right.
/// </remarks>
public class ThreadReportTests : IDisposable
{
    private readonly List<string> _directories = [];

    private string Empty()
    {
        string directory = Path.Combine(Path.GetTempPath(), "janet-report", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        return Path.Combine(directory, "thread-stack.json");
    }

    private string Seeded()
    {
        string path = Empty();

        ThreadItems.Add(path, "cache eviction", notes: "Ruled out the obvious.", next: "query telemetry", refs: ["note.cache"]);
        ThreadItems.Add(path, "cache warming");
        ThreadItems.Add(path, "finished thing", notes: "closed out");
        ThreadItems.Complete(path, new ThreadSelector { Topic = "finished thing" });

        return path;
    }

    [Fact]
    public void TheLeadIsTheFirstNonEmptyLine()
    {
        // Not the first line: notes built by appending start with a blank one, so "first line"
        // is empty for most items that have anything worth reading.
        Assert.Equal("The real first line.", ThreadItems.Lead("\n\n  The real first line.  \nsecond"));
    }

    [Fact]
    public void TheLeadIsEmptyWhenThereAreNoNotes()
    {
        Assert.Equal(string.Empty, ThreadItems.Lead(string.Empty));
        Assert.Equal(string.Empty, ThreadItems.Lead("   \n \n  "));
    }

    [Fact]
    public void ALongLeadIsCappedAndSaysSo()
    {
        string lead = ThreadItems.Lead(new string('x', 500));

        Assert.EndsWith("...", lead);
        Assert.Equal(203, lead.Length);
    }

    [Fact]
    public void EveryItemCarriesTheFullLengthOfNotesItDoesNotCarry()
    {
        ThreadReportResult report = ThreadItems.Report(Seeded());

        ThreadReportItem eviction = report.Items.Single(i => i.Topic == "cache eviction");

        Assert.Equal("Ruled out the obvious.".Length, eviction.NotesLength);
        Assert.Equal("Ruled out the obvious.", eviction.NotesLead);

        ThreadReportItem warming = report.Items.Single(i => i.Topic == "cache warming");

        Assert.Equal(0, warming.NotesLength);
        Assert.Equal(string.Empty, warming.NotesLead);
    }

    [Fact]
    public void TheEnvelopeTotalsWhatItLeftBehind()
    {
        ThreadReportResult report = ThreadItems.Report(Seeded());

        // The total is what makes the omission legible rather than implied.
        Assert.Equal(report.Items.Sum(i => i.NotesLength), report.NotesLength);
        Assert.Equal("Ruled out the obvious.".Length, report.NotesLength);
    }

    [Fact]
    public void CompletedItemsAreExcludedUnlessAskedFor()
    {
        string list = Seeded();

        Assert.DoesNotContain(ThreadItems.Report(list).Items, i => i.Topic == "finished thing");
        Assert.Contains(ThreadItems.Report(list, all: true).Items, i => i.Topic == "finished thing");
    }

    [Fact]
    public void TheTotalCountsOnlyWhatWasReturned()
    {
        string list = Seeded();

        // "closed out" belongs to the completed item, so it is absent from the default total and
        // present in the --all one. A total that counted hidden items would overstate the saving.
        Assert.Equal("Ruled out the obvious.".Length, ThreadItems.Report(list).NotesLength);
        Assert.Equal(
            "Ruled out the obvious.".Length + "closed out".Length,
            ThreadItems.Report(list, all: true).NotesLength);
    }

    [Fact]
    public void AnEmptyListReportsAsEmptyRatherThanAsAnError()
    {
        ThreadReportResult report = ThreadItems.Report(Empty());

        Assert.Equal(0, report.Count);
        Assert.Empty(report.Items);
        Assert.Null(report.Active);
        Assert.Null(report.Error);
        Assert.Equal(0, report.NotesLength);
    }

    [Fact]
    public void ACorruptListIsReportedInBandAndDoesNotThrow()
    {
        // Same contract as Show: this runs in the startup path, so a mangled temp file must not
        // stop a session from beginning.
        string path = Empty();
        File.WriteAllText(path, "{ not json");

        ThreadReportResult report = ThreadItems.Report(path);

        Assert.NotNull(report.Error);
        Assert.Empty(report.Items);
    }

    [Fact]
    public void TheEnvelopeStampsItsContractAndNeverCarriesANoteBody()
    {
        JsonObject envelope = JsonNode.Parse(ThreadJson.Serialize(ThreadItems.Report(Seeded())))!.AsObject();

        // 3 since 2026-09-04, when the envelope gained 'areas' (2 on 2026-09-03, when items
        // gained 'area'). Pinned here as a literal rather than read from the code, so that a bump
        // has to be stated in two places by a person.
        Assert.Equal(3, (int)envelope["contract"]!);

        // The point of the format: no field anywhere holds a note body.
        foreach (JsonNode? item in envelope["items"]!.AsArray())
        {
            Assert.False(item!.AsObject().ContainsKey("notes"));
        }
    }

    /// <summary>
    /// Every item carries the area it is filed under, resolved.
    /// </summary>
    /// <remarks>
    /// Resolved rather than raw, because this is the roster view: a reader scanning it should
    /// see one group called (unfiled) and not a column of blanks that could equally mean "no
    /// area" or "the field was dropped somewhere". What is STORED stays empty -- being
    /// displayed does not file an item.
    /// </remarks>
    [Fact]
    public void EveryReportedItemCarriesItsAreaResolved()
    {
        string path = Seeded();

        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" }, area: "RazorGraph");

        ThreadReportResult report = ThreadItems.Report(path);

        Assert.Equal("RazorGraph", report.Items.Single(i => i.Topic == "cache eviction").Area);
        Assert.Equal(ThreadItems.Unfiled, report.Items.Single(i => i.Topic == "cache warming").Area);
    }

    /// <summary>The report narrows by area, and totals only what it actually returned.</summary>
    [Fact]
    public void TheReportNarrowsToOneAreaAndTotalsOnlyThat()
    {
        string path = Seeded();

        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" }, area: "RazorGraph");

        ThreadReportResult report = ThreadItems.Report(path, area: "RazorGraph");

        Assert.Equal("cache eviction", Assert.Single(report.Items).Topic);
        Assert.Equal(1, report.Count);
        Assert.Equal("Ruled out the obvious.".Length, report.NotesLength);
    }

    /// <summary>
    /// The map: one row per area with open items, counted, sorted by name case-insensitively.
    /// </summary>
    /// <remarks>
    /// Three areas so the ordering is actually tested: '(unfiled)' sorts first on its
    /// parenthesis, and 'gamehub' before 'RazorGraph' only if the comparison ignores case --
    /// ordinal would put every capital before every lower-case letter.
    /// </remarks>
    [Fact]
    public void TheAreasMapCountsOpenItemsPerAreaInNameOrder()
    {
        string path = Seeded();

        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" }, area: "RazorGraph");
        ThreadItems.Add(path, "gamehub scoring", area: "gamehub");
        ThreadItems.Add(path, "gamehub lobby", area: "gamehub");

        ThreadReportResult report = ThreadItems.Report(path);

        Assert.Equal(
            [("(unfiled)", 1), ("gamehub", 2), ("RazorGraph", 1)],
            report.Areas.Select(a => (a.Area, a.Open)));
    }

    /// <summary>'(unfiled)' is a row only while an unlabelled item is OPEN.</summary>
    /// <remarks>
    /// The seed's completed item is unfiled, so this also pins that done items never reach the
    /// map -- not even under all=true, which widens the items and must not widen the counts. A
    /// zero row would read as a category that exists, which is the guess the field avoids.
    /// </remarks>
    [Fact]
    public void UnfiledAppearsInTheMapOnlyWhileSuchItemsAreOpen()
    {
        string path = Seeded();

        Assert.Contains(ThreadItems.Report(path).Areas, a => a.Area == ThreadItems.Unfiled);

        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" }, area: "RazorGraph");
        ThreadItems.Update(path, new ThreadSelector { Topic = "cache warming" }, area: "RazorGraph");

        Assert.DoesNotContain(ThreadItems.Report(path).Areas, a => a.Area == ThreadItems.Unfiled);
        Assert.DoesNotContain(ThreadItems.Report(path, all: true).Areas, a => a.Area == ThreadItems.Unfiled);
        Assert.Equal(2, Assert.Single(ThreadItems.Report(path, all: true).Areas).Open);
    }

    /// <summary>
    /// The map is over the whole list, whatever the selectors narrowed the items to.
    /// </summary>
    /// <remarks>
    /// This is the reason the field exists: startup narrows the report to the session's own
    /// area, and the other projects' backlog has to survive as a summary rather than vanish.
    /// Same rule as 'active', pinned the same way -- narrowed and unnarrowed answers agree.
    /// </remarks>
    [Fact]
    public void TheAreasMapIgnoresTheNarrowing()
    {
        string path = Seeded();

        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" }, area: "RazorGraph");

        ThreadReportResult whole = ThreadItems.Report(path);
        ThreadReportResult narrowed = ThreadItems.Report(path, area: "RazorGraph");
        ThreadReportResult one = ThreadItems.Report(path, topic: "warming");

        Assert.Equal("cache eviction", Assert.Single(narrowed.Items).Topic);
        Assert.Equal(whole.Areas, narrowed.Areas);
        Assert.Equal(whole.Areas, one.Areas);
        Assert.Equal(2, whole.Areas.Count);
    }

    [Fact]
    public void AnEmptyOrUnreadableListHasAnEmptyMapRatherThanNone()
    {
        Assert.Empty(ThreadItems.Report(Empty()).Areas);

        string corrupt = Empty();
        File.WriteAllText(corrupt, "{ not json");

        Assert.Empty(ThreadItems.Report(corrupt).Areas);
    }

    /// <summary>
    /// The envelope and the checked-in contract describe the same shape.
    /// </summary>
    /// <remarks>
    /// Test-OutputContracts.ps1 is the real gate: it validates live samples against the schema
    /// with a JSON Schema validator this project does not reference. What this test can hold
    /// without one is the part that drifts first -- the contract number in three places, and the
    /// key sets of the envelope, an item and an area row against the schema's 'properties' and
    /// 'required' -- so a field added to the code without the schema fails here, in the unit
    /// run, before the commit gate. It reads the LIVE schema out of the repo deliberately: a
    /// copy beside the tests would be one more thing to drift.
    /// </remarks>
    [Fact]
    public void TheEnvelopeAgreesWithTheCheckedInContract()
    {
        string path = Seeded();
        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" }, area: "RazorGraph");

        JsonObject envelope = JsonNode.Parse(ThreadJson.Serialize(ThreadItems.Report(path)))!.AsObject();
        JsonObject schema = JsonNode.Parse(File.ReadAllText(ContractPath("thread-report.schema.json")))!.AsObject();

        int declared = (int)schema["$janet"]!["contract"]!;
        Assert.Equal(declared, (int)schema["properties"]!["contract"]!["const"]!);
        Assert.Equal(declared, (int)envelope["contract"]!);

        AssertSameKeys(schema, envelope, "envelope");
        AssertSameKeys(schema["properties"]!["areas"]!["items"]!.AsObject(), envelope["areas"]!.AsArray().First()!.AsObject(), "areas[]");
        AssertSameKeys(schema["properties"]!["items"]!["items"]!.AsObject(), envelope["items"]!.AsArray().First()!.AsObject(), "items[]");
    }

    /// <summary>
    /// Without leads, every item still states its notes' size and the key is absent, not empty.
    /// </summary>
    /// <remarks>
    /// Added by measurement on 2026-09-04: narrowing the startup report to one area left the
    /// brief at 9,969 characters against a budget of about 8,000, and the leads were 1,827 of
    /// it. The key is OMITTED rather than written empty because an empty lead already means
    /// "this item has no notes", and the two must not be confusable.
    /// </remarks>
    [Fact]
    public void WithoutLeadsEveryItemStillStatesItsNotesLengthAndOmitsTheKey()
    {
        string path = Seeded();

        ThreadReportResult report = ThreadItems.Report(path, lead: false);
        JsonObject envelope = JsonNode.Parse(ThreadJson.Serialize(report))!.AsObject();

        Assert.All(report.Items, i => Assert.Null(i.NotesLead));
        Assert.Equal("Ruled out the obvious.".Length, report.Items.Single(i => i.Topic == "cache eviction").NotesLength);
        Assert.All(envelope["items"]!.AsArray(), i => Assert.False(i!.AsObject().ContainsKey("notesLead")));
        Assert.All(envelope["items"]!.AsArray(), i => Assert.True(i!.AsObject().ContainsKey("notesLength")));

        // And the default still carries it, so the omission is a request rather than a regression.
        Assert.All(
            JsonNode.Parse(ThreadJson.Serialize(ThreadItems.Report(path)))!["items"]!.AsArray(),
            i => Assert.True(i!.AsObject().ContainsKey("notesLead")));
    }

    [Fact]
    public void TheLeadlessEnvelopeAgreesWithTheCheckedInContractToo()
    {
        JsonObject envelope = JsonNode.Parse(ThreadJson.Serialize(ThreadItems.Report(Seeded(), lead: false)))!.AsObject();
        JsonObject schema = JsonNode.Parse(File.ReadAllText(ContractPath("thread-report.schema.json")))!.AsObject();

        AssertSameKeys(schema["properties"]!["items"]!["items"]!.AsObject(), envelope["items"]!.AsArray().First()!.AsObject(), "items[] without leads");
    }

    private static void AssertSameKeys(JsonObject schemaObject, JsonObject produced, string where)
    {
        string[] properties = [.. schemaObject["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];
        string[] required = [.. schemaObject["required"]!.AsArray().Select(r => (string)r!).Order(StringComparer.Ordinal)];
        string[] emitted = [.. produced.Select(p => p.Key).Order(StringComparer.Ordinal)];

        // additionalProperties:false bounds the emitted keys above by 'properties'; 'required'
        // bounds them below. Anything outside either band is a field one side has and the other
        // has not. The only key allowed in the gap is notesLead, optional since contract 3.
        string[] optional = [.. properties.Except(required)];
        Assert.True(optional.Length == 0 || optional.SequenceEqual(["notesLead"]), $"{where}: schema leaves [{string.Join(", ", optional)}] optional; only notesLead may be");
        Assert.True(required.All(emitted.Contains), $"{where}: schema requires [{string.Join(", ", required)}] but the envelope carries [{string.Join(", ", emitted)}]");
        Assert.True(emitted.All(properties.Contains), $"{where}: the envelope carries [{string.Join(", ", emitted)}] but the schema declares only [{string.Join(", ", properties)}]");
    }

    /// <summary>A file under the repo's contracts\ directory, found by walking up from the test binary.</summary>
    private static string ContractPath(string name)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "contracts", name);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"No contracts\\{name} above {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// An area nothing is filed under reports as an ordinary empty envelope, not a throw.
    /// </summary>
    /// <remarks>
    /// Reversed on 2026-09-04 -- this asserted the throw until then. Startup narrows the report
    /// to the session's own project, so opening a session in a repo with nothing on the list
    /// turned the whole run entry into status=error with an exception text where a report
    /// belonged. Lori's call: a repo with nothing in it should return a json like everything
    /// else.
    ///
    /// The whole envelope is asserted, not just the count, because an empty answer is only
    /// honest if every OTHER field still reads correctly: 'error' null says the list was read
    /// fine (an empty answer and an unreadable list are different facts), notesLength zero says
    /// nothing was withheld, and 'areas' still names every area with open work -- which is the
    /// field that makes the empty answer informative rather than merely silent, and the reason
    /// Show, which has no such field, keeps throwing.
    /// </remarks>
    [Fact]
    public void TheReportAnswersAnAreaNothingIsFiledUnderWithAnEmptyEnvelope()
    {
        string path = Seeded();

        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" }, area: "RazorGraph");
        ThreadItems.Add(path, "gamehub scoring", area: "gamehub");

        ThreadReportResult report = ThreadItems.Report(path, area: "SomeOtherRepo");

        Assert.Equal(0, report.Count);
        Assert.Empty(report.Items);
        Assert.Null(report.Error);
        Assert.Equal(0, report.NotesLength);

        Assert.Equal(
            [("(unfiled)", 1), ("gamehub", 1), ("RazorGraph", 1)],
            report.Areas.Select(a => (a.Area, a.Open)));
    }

    /// <summary>
    /// Show still refuses the very area the report now tolerates.
    /// </summary>
    /// <remarks>
    /// The guard on the split, asserted on ONE list so the two answers cannot be explained by
    /// different inputs. Show's envelope has no areas map, so an empty answer there would say
    /// nothing at all and the throw's "Areas in use: ..." is strictly better. If someone ever
    /// makes Show tolerant for symmetry, this is what says no.
    /// </remarks>
    [Fact]
    public void ShowStillRefusesTheAreaTheReportNowTolerates()
    {
        string path = Seeded();

        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" }, area: "RazorGraph");

        Assert.Empty(ThreadItems.Report(path, area: "SomeOtherRepo").Items);

        Assert.Contains(
            "Areas in use: (unfiled), RazorGraph",
            Assert.Throws<GraphException>(() => ThreadItems.Show(path, area: "SomeOtherRepo")).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A list with nothing in it at all still reports, whatever area was asked for.
    /// </summary>
    /// <remarks>
    /// A separate path from the test above and worth its own: the refusal built a DIFFERENT
    /// message here ("The list is empty, so no area is in use yet"), because there were no
    /// known areas to name. Tolerating one branch and not the other would leave the brief
    /// failing on exactly the machine that has never used the thread list.
    /// </remarks>
    [Fact]
    public void AnEmptyListNarrowedToAnyAreaReportsRatherThanThrows()
    {
        ThreadReportResult report = ThreadItems.Report(Empty(), area: "SomeOtherRepo");

        Assert.Equal(0, report.Count);
        Assert.Empty(report.Items);
        Assert.Empty(report.Areas);
        Assert.Null(report.Error);
        Assert.Equal(0, report.NotesLength);
        Assert.Null(report.Active);
    }

    [Fact]
    public void TheReporterAgreesWithShowAboutWhatIsThere()
    {
        // Report reads through Show, and this is what pins that: if they ever disagree about
        // which items are live or which one is active, the cheap view stops being a safe
        // substitute for the expensive one, which is the whole premise.
        string list = Seeded();

        ThreadShowResult shown = ThreadItems.Show(list);
        ThreadReportResult report = ThreadItems.Report(list);

        Assert.Equal(shown.Count, report.Count);
        Assert.Equal(shown.Active, report.Active);
        Assert.Equal(
            shown.Items.Select(i => i.Topic),
            report.Items.Select(i => i.Topic));
    }

    public void Dispose()
    {
        foreach (string directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test run over.
            }
        }

        GC.SuppressFinalize(this);
    }
}
