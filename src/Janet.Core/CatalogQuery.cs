namespace Janet.Core;

/// <summary>What to select from the graph. Mirrors Get-Research.ps1's parameter surface.</summary>
public sealed record CatalogQueryOptions
{
    public IReadOnlyList<string> Id { get; init; } = [];
    public IReadOnlyList<string> Tag { get; init; } = [];
    public string? Kind { get; init; }
    public string? Query { get; init; }

    /// <summary>Cap on ranked free-text results. Explicit selectors and <see cref="All"/> are never capped.</summary>
    public int First { get; init; } = 5;

    public bool All { get; init; }
    public bool Expand { get; init; }
    public int Depth { get; init; } = 1;

    /// <summary>With no other filter, selects every node instead of returning the orientation view.</summary>
    public bool Full { get; init; }

    public bool HasFilter =>
        Id.Count > 0 || Tag.Count > 0 || !string.IsNullOrEmpty(Kind) || !string.IsNullOrEmpty(Query);
}

/// <summary>
/// The standard result envelope. Wrapped rather than a bare array so a consumer can never
/// mistake a shortlist for the whole answer -- <c>truncated</c> is the point of the type.
/// </summary>
public sealed record QueryEnvelope(
    int Returned,
    int TotalMatches,
    bool Truncated,
    IReadOnlyList<ResearchNode> Nodes)
{
    /// <summary>
    /// Links that resolved to nothing, as "source -> target". Deliberately outside the
    /// serialized envelope: the shape is a contract with every existing consumer, and the
    /// PowerShell reports these on the warning stream. Front ends surface them there too.
    /// </summary>
    public IReadOnlyList<string> DanglingLinks { get; init; } = [];
}

/// <summary>The cheap no-filter view: counts by kind, plus the tag index.</summary>
public sealed record OrientationView(
    int Total,
    IReadOnlyList<KeyValuePair<string, int>> Kinds,
    IReadOnlyList<KeyValuePair<string, int>> Tags);

public static class CatalogQuery
{
    public static readonly IReadOnlyList<string> Kinds = ["script", "pattern", "note", "file", "skill"];

    /// <summary>
    /// Ordinal everywhere, not the current culture. PowerShell's Sort-Object is culture-aware,
    /// and the two agree on every id in the current corpus (verified, 92/92) -- but a tool whose
    /// result ordering depends on the machine's locale is not one you can write a parity test
    /// for, and portability is the whole point of this port.
    /// </summary>
    private static readonly StringComparer Ids = StringComparer.Ordinal;

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static OrientationView Orient(ResearchGraph graph)
    {
        List<KeyValuePair<string, int>> kinds =
        [
            .. graph.Nodes
                .GroupBy(n => n.Kind, StringComparer.Ordinal)
                .OrderBy(g => g.Key, Ids)
                .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
        ];

        Dictionary<string, int> tagCounts = new(StringComparer.Ordinal);
        foreach (ResearchNode node in graph.Nodes)
        {
            foreach (string tag in node.Tags)
            {
                tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
            }
        }

        List<KeyValuePair<string, int>> tags = [.. tagCounts.OrderBy(p => p.Key, Ids)];

        return new OrientationView(graph.Nodes.Count, kinds, tags);
    }

    /// <summary>
    /// Scores one node against free-text terms. Weights are ported verbatim from
    /// Get-Research.ps1 -- they were tuned against this corpus and are not arbitrary.
    /// </summary>
    /// <remarks>
    /// Terms are scored independently, so "thread stack debugging" beats a phrase match.
    /// A caveat hit *demotes*: the term appearing in what is wrong with a node is weaker
    /// evidence than it appearing in what the node is for. Caveats never select, or a node
    /// would surface purely for documenting its own breakage. The floor of 1 keeps a demoted
    /// node findable -- a broken tool you can see beats one you cannot.
    ///
    /// Matching is substring, which is what Get-Research.ps1's help documents. The PowerShell
    /// implemented it with -like, so a term containing * or ? behaved as a wildcard by
    /// accident; that edge is not reproduced.
    /// </remarks>
    public static int Score(ResearchNode node, IReadOnlyList<string> terms)
    {
        string caveatText = string.Join(' ', node.Caveats);
        int positive = 0;
        int penalty = 0;

        foreach (string term in terms)
        {
            if (Same(node.Id, term))
            {
                positive += 100;
                continue;
            }

            // Tags are curated, so a tag hit is much stronger evidence than prose.
            if (node.Tags.Any(t => Same(t, term)))
            {
                positive += 40;
            }
            else if (node.Tags.Any(t => Has(t, term)))
            {
                positive += 20;
            }

            if (Has(node.Id, term)) { positive += 15; }
            if (Has(node.Summary, term)) { positive += 10; }
            if (caveatText.Length > 0 && Has(caveatText, term)) { penalty += 5; }
        }

        return positive > 0 ? Math.Max(1, positive - penalty) : 0;
    }

    public static QueryEnvelope Execute(ResearchGraph graph, CatalogQueryOptions options)
    {
        if (!string.IsNullOrEmpty(options.Kind) && !Kinds.Any(k => Same(k, options.Kind)))
        {
            throw new GraphException(
                $"Unknown kind '{options.Kind}'. Valid kinds: {string.Join(", ", Kinds)}");
        }

        bool noFilter = !options.HasFilter;
        string[] terms = string.IsNullOrWhiteSpace(options.Query)
            ? []
            : options.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        List<(ResearchNode Node, int Score)> scored = [];

        foreach (ResearchNode node in graph.Nodes)
        {
            // -Full with no filter means everything.
            bool hit = noFilter;
            int score = 0;

            // Explicit selectors outrank fuzzy ones: an -Id hit must never sort below
            // something that merely mentions the word.
            if (options.Id.Count > 0 && options.Id.Any(id => Same(id, node.Id)))
            {
                hit = true;
                score += 1000;
            }

            if (options.Tag.Count > 0 && options.Tag.Any(t => node.Tags.Any(nt => Same(nt, t))))
            {
                hit = true;
                score += 500;
            }

            if (terms.Length > 0)
            {
                int queryScore = Score(node, terms);
                if (queryScore > 0)
                {
                    hit = true;
                    score += queryScore;
                }
            }

            // -Kind alone selects; combined with anything else it narrows.
            if (!string.IsNullOrEmpty(options.Kind))
            {
                bool kindOnly = options.Id.Count == 0 && options.Tag.Count == 0 && terms.Length == 0;
                if (kindOnly)
                {
                    hit = Same(node.Kind, options.Kind);
                }
                else if (hit && !Same(node.Kind, options.Kind))
                {
                    hit = false;
                }
            }

            if (hit)
            {
                scored.Add((node, score));
            }
        }

        List<(ResearchNode Node, int Score)> ranked =
        [
            .. scored.OrderByDescending(x => x.Score).ThenBy(x => x.Node.Id, Ids)
        ];

        int totalMatches = ranked.Count;

        // Cap only ranked free-text results. Explicit selectors and -All mean the caller
        // already knows what they asked for; truncating those would hide answers.
        bool capped = false;
        if (!options.All && terms.Length > 0 && options.First > 0 && ranked.Count > options.First)
        {
            ranked = [.. ranked.Take(options.First)];
            capped = true;
        }

        List<ResearchNode> order = [.. ranked.Select(r => r.Node)];
        HashSet<string> seen = new(order.Select(n => n.Id), StringComparer.Ordinal);
        List<string> dangling = [];

        if (options.Expand)
        {
            for (int hop = 0; hop < options.Depth; hop++)
            {
                // Snapshot per hop: a node pulled in by this hop is only expanded on the next
                // one, which is what makes -Depth mean hops rather than "follow until closed".
                string[] seeds = [.. order.Select(n => n.Id)];

                foreach (string seedId in seeds)
                {
                    if (!graph.TryGet(seedId, out ResearchNode seed))
                    {
                        continue;
                    }

                    foreach (string link in seed.Links)
                    {
                        if (graph.TryGet(link, out ResearchNode target))
                        {
                            if (seen.Add(link))
                            {
                                order.Add(target);
                            }
                        }
                        else
                        {
                            dangling.Add($"{seedId} -> {link}");
                        }
                    }
                }
            }
        }

        return new QueryEnvelope(order.Count, totalMatches, capped, order) { DanglingLinks = dangling };
    }
}
