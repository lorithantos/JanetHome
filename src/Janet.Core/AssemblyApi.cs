using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace Janet.Core;

/// <summary>One member of a type: what it is, what type it carries, what it is called.</summary>
public sealed record AssemblyMember(string Kind, string Type, string Name);

/// <summary>One public type and the members that survived the filters.</summary>
public sealed record AssemblyType(string Name, string Kind, string? BaseType, IReadOnlyList<AssemblyMember> Members);

/// <summary>Which types and members to report. Both patterns are regexes, matched case-insensitively.</summary>
public sealed record AssemblyApiRequest
{
    public string? Type { get; init; }
    public string? Member { get; init; }

    /// <summary>Include members declared on base types. Off by default: a syntax-node class inherits dozens and the interesting ones are its own.</summary>
    public bool Inherited { get; init; }

    public bool Static { get; init; }
    public int MaxTypes { get; init; } = 40;
}

/// <summary>
/// The API surface, with its truncation and its partial-load state both stated rather than
/// implied.
/// </summary>
public sealed record AssemblyApiResult(
    int Contract,
    string Assembly,
    string Folder,
    int Siblings,
    int TypesLoaded,
    int TypesUnloadable,
    int Matched,
    int Returned,
    bool Truncated,
    IReadOnlyList<AssemblyType> Types,
    string? SiblingWarning,

    /// <summary>
    /// Members left out because a type in their signature could not be resolved. Zero for a
    /// folder holding the whole closure.
    /// </summary>
    int MembersDropped);

/// <summary>
/// Reports a compiled assembly's real API surface, so "what is this library actually called" does
/// not cost a build per wrong guess.
/// </summary>
/// <remarks>
/// Each request gets its own collectible load context, and that is a fix rather than tidiness.
/// The PowerShell used Assembly.LoadFrom, which pins every assembly for the life of the PROCESS,
/// and an agent shell reuses one process across commands. Two failures followed from that, both
/// observed: rebuilding the target and re-running returned the OLD surface, and once a dependency
/// had been loaded from anywhere a later load from a folder MISSING it succeeded -- so a sibling
/// problem reproduced only in a fresh process. A context per request makes both deterministic:
/// every call reads from disk, and a missing sibling is missing every time.
/// </remarks>
public static class AssemblyApi
{
    /// <summary>
    /// Resolves references out of the folder the assembly sits in, which is what turns "could not
    /// load file or assembly" into a working reflection session. Returning null for anything not
    /// found there delegates to the default context, so framework assemblies still resolve.
    /// </summary>
    private sealed class FolderContext(string folder) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            string candidate = Path.Combine(folder, $"{name.Name}.dll");

            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }

    /// <summary>
    /// Takes a path, or a bare assembly name to find under <paramref name="searchRoot"/>.
    /// </summary>
    /// <remarks>
    /// The copy with the most neighbours wins: a publish folder beats a nuget lib folder, which
    /// holds the DLL alone and is where GetTypes() throws.
    /// </remarks>
    public static string ResolveAssemblyPath(string nameOrPath, string searchRoot)
    {
        if (File.Exists(nameOrPath))
        {
            return Path.GetFullPath(nameOrPath);
        }

        string fileName = nameOrPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? nameOrPath
            : $"{nameOrPath}.dll";

        List<string> candidates = Directory.Exists(searchRoot)
            ? [.. Directory.EnumerateFiles(searchRoot, fileName, SearchOption.AllDirectories)]
            : [];

        if (candidates.Count == 0)
        {
            throw new GraphException(
                $"No assembly named '{fileName}' under '{searchRoot}'. Give a full path, or a search root that contains a build output.");
        }

        return candidates
            .OrderByDescending(c => Directory.GetFiles(Path.GetDirectoryName(c)!, "*.dll").Length)
            .First();
    }

    public static AssemblyApiResult Describe(string nameOrPath, string searchRoot, AssemblyApiRequest request)
    {
        (AssemblyApiResult result, WeakReference context) = DescribeInContext(nameOrPath, searchRoot, request);

        // Unload() only SCHEDULES the unload. The assemblies stay mapped, and their files stay
        // locked, until a garbage collection actually collects the context -- which a long-lived
        // server that allocates little may not run for hours. Measured 2026-09-01: seven scratch
        // DLLs held open by janet-mcp after four calls, every build of that folder warning
        // MSB3061, and all seven released by one forced collection. So collect here, until the
        // context is gone or it is clear something is still holding it. The work happens in a
        // separate, non-inlined frame so that no Type or Assembly local of it is still live on
        // this stack while we wait; that is the documented pattern for collectible contexts.
        for (int attempt = 0; context.IsAlive && attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return result;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (AssemblyApiResult Result, WeakReference Context) DescribeInContext(
        string nameOrPath, string searchRoot, AssemblyApiRequest request)
    {
        string assemblyPath = ResolveAssemblyPath(nameOrPath, searchRoot);
        string folder = Path.GetDirectoryName(assemblyPath)!;
        int siblings = Directory.GetFiles(folder, "*.dll").Length;

        FolderContext context = new(folder);
        WeakReference alive = new(context);

        try
        {
            Assembly loaded = context.LoadFromAssemblyPath(assemblyPath);

            Type[] types;
            int unloadable = 0;
            string? warning = null;

            try
            {
                types = loaded.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // A partial answer beats none, but say how partial it is.
                types = [.. ex.Types.OfType<Type>()];
                unloadable = ex.LoaderExceptions.Length;

                if (siblings <= 1)
                {
                    warning = $"'{folder}' holds {siblings} assembly: dependencies cannot resolve there. " +
                        "Point -SearchRoot at a build or publish output instead of a nuget lib folder.";
                }
            }

            List<Type> matched = [.. types
                .Where(t => t.IsPublic && (string.IsNullOrEmpty(request.Type) || Regex.IsMatch(t.Name, request.Type, RegexOptions.IgnoreCase)))
                .OrderBy(t => t.Name, StringComparer.InvariantCultureIgnoreCase)];

            List<Type> shown = [.. matched.Take(request.MaxTypes)];

            BindingFlags binding = BindingFlags.Public | BindingFlags.Instance;
            if (request.Static)
            {
                binding |= BindingFlags.Static;
            }

            if (!request.Inherited)
            {
                binding |= BindingFlags.DeclaredOnly;
            }

            List<AssemblyType> report = [];
            int dropped = 0;

            foreach (Type type in shown)
            {
                (AssemblyType described, int missed) = Describe(type, binding, request.Member);
                report.Add(described);
                dropped += missed;
            }

            AssemblyApiResult result = new(
                Contract: 1,
                Assembly: Path.GetFileName(assemblyPath),
                Folder: folder,
                Siblings: siblings,
                TypesLoaded: types.Length,
                TypesUnloadable: unloadable,
                Matched: matched.Count,
                // Truncation is stated, never silent: a capped list that looks complete is the
                // failure this whole catalog exists to avoid.
                Returned: shown.Count,
                Truncated: matched.Count > shown.Count,
                Types: report,
                SiblingWarning: warning,
                MembersDropped: dropped);

            return (result, alive);
        }
        finally
        {
            // Nothing above escapes as a Type, only strings, so once this frame is gone the
            // context has no live references and the caller can collect it.
            context.Unload();
        }
    }

    /// <summary>
    /// A type's members, and how many had to be dropped because something in their signature
    /// could not be resolved.
    /// </summary>
    /// <remarks>
    /// The original script promised "a partial answer beats none" and delivered it one level too
    /// high: it recovered from a ReflectionTypeLoadException on GetTypes, then died reading
    /// PropertyType.Name on a type that HAD loaded but whose property type lived in a missing
    /// assembly. Pointed at a bare nuget lib folder in a fresh process it printed the sibling
    /// warning and then threw "The property 'Name' cannot be found on this object" -- so the
    /// documented behaviour was not the actual behaviour, and only in a fresh process, which is
    /// why it survived. The recovery now runs at the level the failure happens at.
    /// </remarks>
    private static (AssemblyType Type, int Dropped) Describe(Type type, BindingFlags binding, string? memberPattern)
    {
        List<AssemblyMember> members = [];
        int dropped = 0;

        MemberInfo[] declared;
        try
        {
            declared = type.GetMembers(binding);
        }
        catch (Exception ex) when (IsUnresolvable(ex))
        {
            declared = [];
            dropped++;
        }

        foreach (MemberInfo member in declared)
        {
            if (!string.IsNullOrEmpty(memberPattern) && !Regex.IsMatch(member.Name, memberPattern, RegexOptions.IgnoreCase))
            {
                continue;
            }

            if (member.MemberType == MemberTypes.Constructor)
            {
                continue;
            }

            // get_X / set_X / op_Equality and friends: the compiler's spelling of members already
            // listed properly. Printing both doubles the output and hides the real surface in
            // accessor noise.
            if (member is MethodInfo { IsSpecialName: true })
            {
                continue;
            }

            string memberType;
            try
            {
                memberType = member switch
                {
                    PropertyInfo property => property.PropertyType.Name,
                    FieldInfo field => field.FieldType.Name,
                    MethodInfo method => method.ReturnType.Name,
                    _ => "",
                };
            }
            catch (Exception ex) when (IsUnresolvable(ex))
            {
                // Dropped rather than listed with a blank type: a member whose type is unknown
                // reads as a member with no type, and the count is the honest version.
                dropped++;
                continue;
            }

            members.Add(new AssemblyMember(member.MemberType.ToString(), memberType, member.Name));
        }

        string kind;
        string? baseName;
        try
        {
            // IsEnum reads the base type, so this is the same exposure as BaseType below.
            kind = type switch
            {
                { IsInterface: true } => "interface",
                { IsEnum: true } => "enum",
                { IsAbstract: true } => "abstract class",
                _ => "class",
            };

            baseName = type.BaseType?.Name;
        }
        catch (Exception ex) when (IsUnresolvable(ex))
        {
            kind = "class";
            baseName = null;
            dropped++;
        }

        AssemblyType described = new(
            type.Name,
            kind,
            baseName,
            [.. members
                .OrderBy(m => m.Kind, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(m => m.Name, StringComparer.InvariantCultureIgnoreCase)]);

        return (described, dropped);
    }

    /// <summary>
    /// The three ways a name in a signature fails to resolve when a dependency is not beside the
    /// assembly. Narrow on purpose: anything else is a real fault and must not be counted and
    /// swallowed.
    /// </summary>
    private static bool IsUnresolvable(Exception ex) =>
        ex is FileNotFoundException or FileLoadException or TypeLoadException;
}
