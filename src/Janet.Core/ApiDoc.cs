using System.Text.RegularExpressions;
using System.Xml;

namespace Janet.Core;

/// <summary>One documented parameter.</summary>
public sealed record ApiParameter(string Name, string Doc);

/// <summary>One documented exception, from a &lt;exception cref="..."&gt; element.</summary>
public sealed record ApiException(string Type, string Doc);

/// <summary>
/// One member of the documented surface. Mutable in two fields only, because resolving
/// &lt;inheritdoc/&gt; is a second pass over the whole table -- a member's summary can come from a
/// member parsed later in the file, so it cannot be settled while parsing.
/// </summary>
public sealed class ApiMember
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string Declaring { get; init; }
    public required string Signature { get; init; }
    public string Summary { get; set; } = "";
    public string Returns { get; set; } = "";
    public string Value { get; init; } = "";
    public IReadOnlyList<ApiParameter> Parameters { get; init; } = [];
    public string Inheritdoc { get; set; } = "";
    public string Remarks { get; init; } = "";
    public IReadOnlyList<ApiException> Exceptions { get; init; } = [];
    public IReadOnlyList<ApiParameter> TypeParams { get; init; } = [];
}

/// <summary>What to look for. Every field is optional; all of them empty is the orientation view.</summary>
public sealed record ApiDocRequest
{
    public string? Query { get; init; }
    public IReadOnlyList<string> Ids { get; init; } = [];
    public string? Kind { get; init; }
    public string? Type { get; init; }
    public int First { get; init; } = 5;
    public bool All { get; init; }
    public bool Full { get; init; }
}

/// <summary>The ranked shortlist, with its own truncation reported.</summary>
public sealed record ApiDocResult(
    string Source,
    int Returned,
    int TotalMatches,
    bool Truncated,
    IReadOnlyList<ApiMember> Members);

/// <summary>The cheap view: what is in this API and which types are biggest.</summary>
public sealed record ApiDocOrientation(
    string Source,
    int Total,
    IReadOnlyList<KeyValuePair<string, int>> Kinds,
    IReadOnlyList<KeyValuePair<string, int>> Types);

/// <summary>
/// Queries a .NET XML documentation file the way the catalog is queried: parsed, ranked, and
/// honest about truncation, rather than grepped. The file is one stream of hard-wrapped indented
/// member elements, so a grep match costs dozens of context lines and the answer arrives split
/// across them.
/// </summary>
public static class ApiDoc
{
    private static readonly Dictionary<char, string> KindByPrefix = new()
    {
        ['T'] = "Type",
        ['M'] = "Method",
        ['P'] = "Property",
        ['F'] = "Field",
        ['E'] = "Event",
    };

    // ---- locating the documentation file -------------------------------------------------

    /// <summary>
    /// Finds a package's XML docs in the local NuGet cache: newest version, newest target
    /// framework, so callers never hunt for the path.
    /// </summary>
    public static string ResolvePath(string packageId, string? wantVersion, string? wantTfm)
    {
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget",
            "packages");

        if (!Directory.Exists(cache))
        {
            throw new GraphException($"No NuGet package cache at {cache}. Use -Path.");
        }

        // Shortest name first: a prefix match on 'LiveChartsCore' must not land on
        // 'LiveChartsCore.SkiaSharpView' when the exact package is present.
        DirectoryInfo? dir = new DirectoryInfo(cache).GetDirectories()
            .Where(d => d.Name.StartsWith(packageId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Name.Length)
            .FirstOrDefault();

        if (dir is null)
        {
            throw new GraphException($"No cached package matching '{packageId}'. Use -Path.");
        }

        List<DirectoryInfo> versions = [.. dir.GetDirectories()];
        if (!string.IsNullOrEmpty(wantVersion))
        {
            versions = [.. versions.Where(v => v.Name == wantVersion)];
            if (versions.Count == 0)
            {
                throw new GraphException($"Version '{wantVersion}' not cached for {dir.Name}.");
            }
        }

        // Newest first, but only where the folder name actually parses as a version; prerelease
        // tags ('2.0.5-beta') do not, and must not sort as though they do.
        List<DirectoryInfo> ordered = [.. versions.OrderByDescending(v =>
            Version.TryParse(v.Name, out Version? parsed) ? parsed : new Version(0, 0))];

        foreach (DirectoryInfo version in ordered)
        {
            string libRoot = Path.Combine(version.FullName, "lib");
            if (!Directory.Exists(libRoot))
            {
                continue;
            }

            IEnumerable<DirectoryInfo> frameworks = new DirectoryInfo(libRoot).GetDirectories();
            if (!string.IsNullOrEmpty(wantTfm))
            {
                frameworks = frameworks.Where(f => f.Name == wantTfm);
            }

            List<DirectoryInfo> ranked = [.. frameworks
                .OrderByDescending(f => TfmRank(f.Name))
                .ThenByDescending(f => f.Name, StringComparer.InvariantCultureIgnoreCase)];

            foreach (DirectoryInfo framework in ranked)
            {
                FileInfo[] xml = framework.GetFiles("*.xml");
                if (xml.Length == 0)
                {
                    continue;
                }

                FileInfo? preferred = xml.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f.Name).Equals(dir.Name, StringComparison.OrdinalIgnoreCase));

                return preferred?.FullName ?? xml[0].FullName;
            }
        }

        throw new GraphException($"Package '{dir.Name}' is cached but ships no XML documentation.");
    }

    // A modern TFM carries the same docs as the legacy ones and is the build the caller is almost
    // certainly compiling against.
    private static int TfmRank(string name)
    {
        if (Regex.IsMatch(name, @"^net\d+\.\d+-"))
        {
            return 400;
        }

        if (Regex.IsMatch(name, @"^net\d+\.\d+"))
        {
            return 300;
        }

        return name.StartsWith("netstandard", StringComparison.Ordinal) ? 200 : 100;
    }

    // ---- turning doc markup into prose ---------------------------------------------------

    /// <summary>
    /// The whole reason this exists. Raw doc XML is markup wrapped for a generator: the cref
    /// attribute carries the information while the tag carries none, and the indentation is
    /// layout rather than meaning. Grep hands you all of it.
    /// </summary>
    internal static string ToDocText(string? markup)
    {
        if (string.IsNullOrEmpty(markup))
        {
            return "";
        }

        string text = markup;
        text = Regex.Replace(text, @"<see\s+cref=""[A-Z]:([^""]+)""\s*/>", "$1");
        text = Regex.Replace(text, @"<see\s+cref=""[A-Z]:([^""]+)""\s*>.*?</see>", "$1", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<see\s+langword=""([^""]+)""\s*/>", "$1");
        text = Regex.Replace(text, @"<(?:paramref|typeparamref)\s+name=""([^""]+)""\s*/>", "$1");
        text = Regex.Replace(text, @"</?(?:c|para|b|i|list|item|term|description|code)[^>]*>", " ");
        text = Regex.Replace(text, "<[^>]+>", " ");

        // Ampersand last, deliberately: unescaping it first would turn '&amp;lt;' into '<' and
        // silently change text the author escaped on purpose.
        text = text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'");
        text = text.Replace("&amp;", "&");

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string ChildText(XmlNode member, string name)
    {
        XmlNode? node = member.SelectSingleNode(name);

        return node is null ? "" : ToDocText(node.InnerXml);
    }

    // ---- parsing member names -------------------------------------------------------------

    internal sealed record ParsedName(string Kind, string Declaring, string Simple, string Args, string ReturnSuffix);

    internal static ParsedName SplitMemberName(string raw)
    {
        char prefix = raw[0];
        string rest = raw[2..];

        // A conversion operator encodes its return type as '~T' after the argument list; that
        // belongs to the signature, not to the name.
        string returnSuffix = "";
        int tilde = rest.IndexOf('~');
        if (tilde >= 0)
        {
            returnSuffix = rest[(tilde + 1)..];
            rest = rest[..tilde];
        }

        string args = "";
        int paren = rest.IndexOf('(');
        if (paren >= 0)
        {
            args = rest[(paren + 1)..].TrimEnd(')');
            rest = rest[..paren];
        }

        string declaring;
        string simple;
        if (prefix == 'T')
        {
            declaring = rest;
            simple = rest.Split('.')[^1];
        }
        else
        {
            int lastDot = rest.LastIndexOf('.');
            if (lastDot < 0)
            {
                declaring = "";
                simple = rest;
            }
            else
            {
                declaring = rest[..lastDot];
                simple = rest[(lastDot + 1)..];
            }
        }

        return new ParsedName(KindByPrefix[prefix], declaring, simple, args, returnSuffix);
    }

    /// <summary>
    /// Argument lists in the file are fully qualified, which is unreadable and is not what the
    /// caller is deciding between. Namespaces are dropped; generic arity is kept.
    /// </summary>
    internal static string FormatSignature(ParsedName parsed)
    {
        if (string.IsNullOrEmpty(parsed.Args))
        {
            return parsed.Kind == "Method" ? $"{parsed.Simple}()" : parsed.Simple;
        }

        // Split on top-level commas only: a generic argument carries its own.
        int depth = 0;
        System.Text.StringBuilder current = new();
        List<string> parts = [];

        foreach (char ch in parsed.Args)
        {
            switch (ch)
            {
                case '{':
                case '(':
                    depth++;
                    current.Append(ch);
                    break;
                case '}':
                case ')':
                    depth--;
                    current.Append(ch);
                    break;
                case ',':
                    if (depth == 0)
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(ch);
                    }

                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        // Keep only the last namespace segment of each type, inside generics too.
        IEnumerable<string> shortened = parts.Select(p => Regex.Replace(p.Trim(), @"[\w\.]+\.(\w+)", "$1"));

        return $"{parsed.Simple}({string.Join(", ", shortened)})";
    }

    // ---- building the member table ---------------------------------------------------------

    /// <summary>
    /// Parses every documented member and resolves &lt;inheritdoc/&gt; by following the chain.
    /// </summary>
    /// <param name="xml">The document's text.</param>
    /// <param name="source">Where it came from, for the error message only.</param>
    /// <param name="full">Also collect remarks, exceptions and type parameters.</param>
    public static IReadOnlyList<ApiMember> Parse(string xml, string source, bool full)
    {
        XmlDocument doc = new();
        try
        {
            doc.LoadXml(xml);
        }
        catch (XmlException ex)
        {
            throw new GraphException($"Not a valid XML document ({source}): {ex.Message}");
        }

        List<ApiMember> members = [];
        Dictionary<string, ApiMember> byName = [];

        foreach (XmlNode node in doc.SelectNodes("/doc/members/member") ?? (XmlNodeList)doc.CreateDocumentFragment().ChildNodes)
        {
            string raw = node.Attributes?["name"]?.Value ?? "";
            if (raw.Length < 3 || !KindByPrefix.ContainsKey(raw[0]))
            {
                continue;
            }

            ParsedName parsed = SplitMemberName(raw);

            List<ApiParameter> parameters = [];
            foreach (XmlNode p in node.SelectNodes("param") ?? (XmlNodeList)doc.CreateDocumentFragment().ChildNodes)
            {
                parameters.Add(new ApiParameter(p.Attributes?["name"]?.Value ?? "", ToDocText(p.InnerXml)));
            }

            List<ApiException> exceptions = [];
            List<ApiParameter> typeParams = [];
            string remarks = "";

            if (full)
            {
                remarks = ChildText(node, "remarks");

                foreach (XmlNode e in node.SelectNodes("exception") ?? (XmlNodeList)doc.CreateDocumentFragment().ChildNodes)
                {
                    string cref = e.Attributes?["cref"]?.Value ?? "";
                    exceptions.Add(new ApiException(Regex.Replace(cref, "^[A-Z]:", ""), ToDocText(e.InnerXml)));
                }

                foreach (XmlNode tp in node.SelectNodes("typeparam") ?? (XmlNodeList)doc.CreateDocumentFragment().ChildNodes)
                {
                    typeParams.Add(new ApiParameter(tp.Attributes?["name"]?.Value ?? "", ToDocText(tp.InnerXml)));
                }
            }

            string returns = ChildText(node, "returns");
            if (!string.IsNullOrEmpty(parsed.ReturnSuffix))
            {
                returns = $"{parsed.ReturnSuffix}. {returns}".Trim();
            }

            // <inheritdoc/> is why grepping this file misleads: the member you searched for
            // documents nothing, and the prose lives on the interface it implements.
            string inheritdoc = "";
            if (node.SelectSingleNode("inheritdoc") is XmlElement inherit)
            {
                inheritdoc = inherit.GetAttribute("cref");
            }

            ApiMember member = new()
            {
                Id = raw,
                Kind = parsed.Kind,
                Name = parsed.Simple,
                Declaring = parsed.Declaring,
                Signature = FormatSignature(parsed),
                Summary = ChildText(node, "summary"),
                Returns = returns,
                Value = ChildText(node, "value"),
                Parameters = parameters,
                Inheritdoc = inheritdoc,
                Remarks = remarks,
                Exceptions = exceptions,
                TypeParams = typeParams,
            };

            members.Add(member);
            byName.TryAdd(raw, member);
        }

        if (members.Count == 0)
        {
            throw new GraphException($"No documented members found in {source}");
        }

        ResolveInheritdoc(members, byName);

        return members;
    }

    /// <summary>
    /// Follows the &lt;inheritdoc/&gt; chain rather than one hop.
    /// </summary>
    /// <remarks>
    /// One hop looks sufficient and is not: an override typically points at the base class, whose
    /// own docs are themselves an inheritdoc onto the interface, so BarSeries`3 -> Series`3 ->
    /// ISeries is the ordinary shape rather than an exotic one. Resolving a single hop also makes
    /// the result depend on document order, because a member resolved before its target was
    /// filled in stays empty. Every hop is an explicit cref the author wrote, so following them is
    /// reading the documentation rather than guessing at it.
    ///
    /// Depth-capped and visited-guarded: circular crefs exist in the wild and must not hang the
    /// query. An unresolved reference is reported, never silently dropped -- "documented
    /// elsewhere, and here is where" beats a blank.
    /// </remarks>
    private static void ResolveInheritdoc(List<ApiMember> members, Dictionary<string, ApiMember> byName)
    {
        foreach (ApiMember member in members)
        {
            if (!string.IsNullOrEmpty(member.Summary) || string.IsNullOrEmpty(member.Inheritdoc))
            {
                continue;
            }

            HashSet<string> seen = [member.Id];
            string cursor = member.Inheritdoc;
            string origin = "";

            for (int hop = 0; hop < 5; hop++)
            {
                if (string.IsNullOrEmpty(cursor) || !seen.Add(cursor))
                {
                    break;
                }

                if (!byName.TryGetValue(cursor, out ApiMember? sourceMember))
                {
                    break;
                }

                if (!string.IsNullOrEmpty(sourceMember.Summary))
                {
                    member.Summary = sourceMember.Summary;
                    origin = cursor;
                    break;
                }

                cursor = sourceMember.Inheritdoc;
            }

            member.Inheritdoc = origin.Length > 0 ? $"inherited from {origin}" : $"see {member.Inheritdoc}";
        }
    }

    // ---- orientation ------------------------------------------------------------------------

    public static ApiDocOrientation Orient(IReadOnlyList<ApiMember> members, string source)
    {
        List<KeyValuePair<string, int>> kinds = [.. members
            .GroupBy(m => m.Kind)
            .OrderBy(g => g.Key, StringComparer.InvariantCultureIgnoreCase)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))];

        // Ties broken by name, which the original did not do. PowerShell's Sort-Object is
        // unstable unless -Stable is passed, so two types with the same member count came back in
        // whatever order Array.Sort happened to leave them -- reproducible for one input and
        // arbitrary across any change to the file. The caller reads this map top down, so an
        // order that shifts when an unrelated member is added is worse than a boring one.
        List<KeyValuePair<string, int>> types = [.. members
            .Where(m => !string.IsNullOrEmpty(m.Declaring))
            .GroupBy(m => m.Declaring)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.InvariantCultureIgnoreCase)
            .Take(25)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))];

        return new ApiDocOrientation(source, members.Count, kinds, types);
    }

    // ---- scoring ------------------------------------------------------------------------------

    /// <summary>
    /// The member's own name is what the caller is trying to recall, so it outranks the type it
    /// lives on, which outranks prose about either.
    /// </summary>
    /// <remarks>
    /// Public rather than internal so the weights can be asserted directly. They do not reach the
    /// envelope, so a golden that compares shortlists only notices a reweighting large enough to
    /// reorder something -- 40 to 41 failed nothing.
    /// </remarks>
    public static int Score(ApiMember member, IReadOnlyList<string> terms)
    {
        int score = 0;

        foreach (string term in terms)
        {
            if (member.Name.Equals(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
                continue;
            }

            if (Has(member.Name, term))
            {
                score += 40;
            }

            if (Has(member.Declaring, term))
            {
                score += 25;
            }

            if (Has(member.Summary, term))
            {
                score += 10;
            }

            foreach (ApiParameter parameter in member.Parameters)
            {
                if (Has(parameter.Name, term) || Has(parameter.Doc, term))
                {
                    score += 5;
                    break;
                }
            }
        }

        // An undocumented member that matched only on its name is still the right answer
        // sometimes, but it loses every tie against one that explains itself.
        if (score > 0 && string.IsNullOrEmpty(member.Summary))
        {
            score = Math.Max(1, score - 15);
        }

        return score;
    }

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // ---- query ---------------------------------------------------------------------------------

    public static ApiDocResult Query(IReadOnlyList<ApiMember> members, string source, ApiDocRequest request)
    {
        string[] terms = string.IsNullOrWhiteSpace(request.Query)
            ? []
            : [.. request.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];

        bool hasIds = request.Ids.Count > 0;
        bool selectorOnly = !hasIds && string.IsNullOrEmpty(request.Query);

        List<(ApiMember Member, int Score)> scored = [];

        foreach (ApiMember member in members)
        {
            bool hit = false;
            int score = 0;

            if (hasIds)
            {
                string bare = Regex.Replace(member.Id, "^[A-Z]:", "");
                foreach (string wanted in request.Ids)
                {
                    if (member.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                        bare.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = true;
                        score += 1000;
                        break;
                    }
                }
            }

            if (terms.Length > 0)
            {
                int queryScore = Score(member, terms);
                if (queryScore > 0)
                {
                    hit = true;
                    score += queryScore;
                }
            }

            // -Kind and -Type select on their own and narrow when combined.
            if (!string.IsNullOrEmpty(request.Kind))
            {
                if (selectorOnly)
                {
                    hit = member.Kind.Equals(request.Kind, StringComparison.OrdinalIgnoreCase);
                }
                else if (hit && !member.Kind.Equals(request.Kind, StringComparison.OrdinalIgnoreCase))
                {
                    hit = false;
                }
            }

            if (!string.IsNullOrEmpty(request.Type))
            {
                bool typeHit = Has(member.Declaring, request.Type);
                if (selectorOnly && string.IsNullOrEmpty(request.Kind))
                {
                    hit = typeHit;
                }
                else if (selectorOnly)
                {
                    hit = hit && typeHit;
                }
                else if (hit && !typeHit)
                {
                    hit = false;
                }
            }

            if (hit)
            {
                scored.Add((member, score));
            }
        }

        List<ApiMember> ranked = [.. scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Member.Id, StringComparer.InvariantCultureIgnoreCase)
            .Select(s => s.Member)];

        int totalMatches = ranked.Count;

        // The cap applies to free-text only. A -Type or -Kind selector is an explicit request for
        // a known set, and silently returning five of it would be the truncation this whole
        // catalog exists to avoid.
        bool capped = !request.All && terms.Length > 0 && request.First > 0 && ranked.Count > request.First;
        if (capped)
        {
            ranked = [.. ranked.Take(request.First)];
        }

        return new ApiDocResult(source, ranked.Count, totalMatches, capped, ranked);
    }
}
