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

        Assert.Equal(1, (int)envelope["contract"]!);

        // The point of the format: no field anywhere holds a note body.
        foreach (JsonNode? item in envelope["items"]!.AsArray())
        {
            Assert.False(item!.AsObject().ContainsKey("notes"));
        }
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
