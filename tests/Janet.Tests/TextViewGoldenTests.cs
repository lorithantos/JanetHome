using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The formatted view has to match too.
/// </summary>
/// <remarks>
/// It is not a nicety: measured, it is ~75% of the JSON's size for a multi-node result, so it is
/// the cheaper projection for a model scanning a shortlist -- and it is what a person reads at a
/// terminal. A port that dropped it would be a regression dressed as a port, which is nearly
/// what happened: --text was advertised by the CLI and silently returned JSON until these cases
/// existed.
/// </remarks>
public class TextViewGoldenTests
{
    public static TheoryData<string> Labels() => [.. Cases.TextViews.Select(v => v.Label)];

    [Theory]
    [MemberData(nameof(Labels))]
    public void MatchesTheRecordedAnswer(string label)
    {
        Cases.Read view = Cases.TextViews.Single(v => v.Label == label);
        ResearchGraph graph = ResearchGraph.Load(Fixture.Corpus);

        string actual;

        if (view.Args.Length == 0)
        {
            actual = CatalogText.Render(CatalogQuery.Orient(graph));
        }
        else
        {
            CatalogQueryOptions options = CatalogGoldenTests.Parse(view.Args);

            actual = CatalogText.Render(
                CatalogQuery.Execute(graph, options),
                ranked: !string.IsNullOrWhiteSpace(options.Query),
                full: options.Full);
        }

        GraphAssert.SameLines(label, Fixture.ReadGolden("text", label, ".txt"), actual);
    }
}
