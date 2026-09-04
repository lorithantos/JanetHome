using System.ComponentModel;
using Janet.Core;
using ModelContextProtocol.Server;

namespace Janet.Mcp;

/// <summary>
/// Two ways to find out what a library is actually called, instead of guessing and rebuilding.
/// </summary>
/// <remarks>
/// Both answer the same question from different evidence, and which one is right depends on what
/// exists: the XML docs say what a member is FOR and are often incomplete or absent, while the
/// compiled assembly says what is THERE and never lies about a name. Ask api_doc_query first
/// when there is prose to be had, and assembly_api when the answer is "what is this member
/// called" or when the package ships no documentation at all.
/// </remarks>
[McpServerToolType]
public static class ApiTools
{
    [McpServerTool(Name = "api_doc_query")]
    [Description(
        "Search a .NET library's XML documentation the way research_query searches the catalog: " +
        "ranked, parsed into members, with the doc markup flattened to prose. Use this instead of " +
        "grepping the .xml -- the file is one stream of hard-wrapped indented elements, so a grep " +
        "match costs dozens of context lines and the answer arrives split across them. With a " +
        "package and no query you get the cheap orientation view: counts by kind and the largest " +
        "types. Free-text results are RANKED and capped, so check 'truncated'; a selector (type, " +
        "kind, id) is never capped because it is a request for a known set. Members documented " +
        "with <inheritdoc/> are resolved by following the chain, and the 'inheritdoc' field says " +
        "where the prose came from -- a bare <inheritdoc/> with no cref cannot be resolved from " +
        "an XML file alone and honestly reports as undocumented. A result over the result budget " +
        "(100,000 characters by default) is REFUSED rather than cut, with a message naming the " +
        "size and the selectors to use instead.")]
    public static string DocQuery(
        [Description("NuGet package id, resolved from the local package cache. Prefix match; newest version and newest target framework win.")]
        string? package = null,
        [Description("An explicit .xml documentation file, bypassing package resolution entirely.")]
        string? path = null,
        [Description("Free text, scored across member name, declaring type, summary and parameter docs. Terms score independently.")]
        string? query = null,
        [Description("Exact member ids, with or without the 'M:'/'T:' prefix.")]
        string[]? id = null,
        [Description("One of Type, Method, Property, Field, Event.")]
        string? kind = null,
        [Description("Restrict to members whose declaring type contains this substring.")]
        string? type = null,
        [Description("Cap on ranked free-text results. Default 5.")]
        int first = 5,
        [Description("Return every match instead of a ranked shortlist.")]
        bool all = false,
        [Description("Also return remarks, exceptions and type parameters.")]
        bool full = false,
        [Description("Pin a package version instead of taking the newest cached one.")]
        string? version = null,
        [Description("Pin a target framework instead of taking the newest one.")]
        string? tfm = null)
    {
        string source = path ?? ApiDoc.ResolvePath(
            package ?? throw new GraphException("Pass package or path."),
            version,
            tfm);

        if (!File.Exists(source))
        {
            throw new GraphException($"Documentation file not found: {source}");
        }

        ApiDocRequest request = new()
        {
            Query = query,
            Ids = id ?? [],
            Kind = kind,
            Type = type,
            First = first,
            All = all,
            Full = full,
        };

        IReadOnlyList<ApiMember> members = ApiDoc.Parse(File.ReadAllText(source), source, full);

        bool noFilter = request.Ids.Count == 0
            && string.IsNullOrEmpty(query)
            && string.IsNullOrEmpty(kind)
            && string.IsNullOrEmpty(type);

        return noFilter
            ? ApiDocJson.Serialize(ApiDoc.Orient(members, source))
            : ApiDocJson.Serialize(ApiDoc.Query(members, source, request));
    }

    [McpServerTool(Name = "assembly_api")]
    [Description(
        "Report what a compiled assembly actually declares: public types, their kind and base " +
        "type, and each type's members with the type each one carries. Use this when a wrong " +
        "guess would cost a build, or when the package ships no XML documentation. Point " +
        "searchRoot at a BUILD OR PUBLISH OUTPUT, not a nuget lib folder: a folder holding one " +
        "assembly cannot resolve its dependencies, and the answer comes back partial with " +
        "'siblingWarning' saying so and 'typesUnloadable'/'membersDropped' counting what was " +
        "lost. Inherited and static members are excluded by default, because a class inherits " +
        "dozens and the interesting ones are its own. 'truncated' reports a capped list. A result " +
        "over the result budget (100,000 characters by default) is REFUSED rather than cut, with " +
        "a message naming the size and the selectors to use instead.")]
    public static string Assembly(
        [Description("Path to the .dll, or a bare assembly name to find under searchRoot.")]
        string assembly,
        [Description("Directory to search when 'assembly' is a name. The candidate with the most sibling assemblies wins, since that is the one whose dependencies resolve.")]
        string searchRoot = ".",
        [Description("Regex selecting type names. Omit to list every public type, which for a large library is usually not what you want.")]
        string? type = null,
        [Description("Regex selecting member names within the matched types.")]
        string? member = null,
        [Description("Include members declared on base types.")]
        bool inherited = false,
        [Description("Include static members.")]
        bool @static = false,
        [Description("Cap on types returned. Default 40; 'truncated' says when it bit.")]
        int maxTypes = 40) =>
        AssemblyApiJson.Serialize(
            AssemblyApi.Describe(assembly, searchRoot, new AssemblyApiRequest
            {
                Type = type,
                Member = member,
                Inherited = inherited,
                Static = @static,
                MaxTypes = maxTypes,
            }),
            pretty: false);
}
