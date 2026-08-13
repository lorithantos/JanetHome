using Janet.Core;
using Xunit;

namespace Janet.Tests;

// ---- the surface under test -------------------------------------------------------------------
//
// Real top-level public types in this assembly, rather than a checked-in DLL or a fixture project.
// Type.IsPublic is false for a nested type, so these cannot be tucked inside the test class; and a
// committed binary would be a fixture nobody can read the source of.

/// <summary>An interface, to pin the 'interface' kind and the DeclaredOnly default.</summary>
public interface ISurfaceProbe
{
    void Ping();
}

/// <summary>An enum, which must report as 'enum' rather than as the class its base makes it.</summary>
public enum SurfaceProbeMode
{
    Off,
    On,
}

/// <summary>An abstract base, to pin 'abstract class' and to have something to inherit.</summary>
public abstract class SurfaceProbeBase
{
    public int InheritedCount => 1;
}

/// <summary>The class carrying one of everything the reporter has a branch for.</summary>
public class SurfaceProbe : SurfaceProbeBase, ISurfaceProbe
{
    public int Field;

    public static int StaticField;

    public SurfaceProbe()
    {
    }

    public string Name { get; set; } = "";

    public event EventHandler? Fired;

    public void Ping() => Fired?.Invoke(this, EventArgs.Empty);

    public static SurfaceProbe Create() => new();

    public static SurfaceProbe operator +(SurfaceProbe left, SurfaceProbe right) => left;
}

/// <summary>
/// The reflection half of the port, tested against types this assembly actually declares.
/// </summary>
/// <remarks>
/// Not golden-tested, and the reason is worth stating rather than leaving as an omission. The
/// envelope carries an absolute folder path and a count of the DLLs beside the assembly, so a
/// recorded answer would be pinned to one machine's build output -- and the only assembly always
/// available to compare against is one this repo rebuilds, which would rot the golden on the next
/// edit. Parity with the PowerShell was checked live instead, across the flag space and on both a
/// healthy build output and a bare nuget lib folder, and the result is in the commit message.
///
/// ONE PATH HERE IS NOT COVERED, and mutation testing says so: making an unresolvable member list
/// with a blank type instead of being dropped and counted fails nothing below. Reaching it needs
/// an assembly whose dependency is genuinely absent, and inside the test host it is not enough to
/// withhold the file -- anything the runner has already loaded resolves through the default
/// context anyway, which is the same pinning trap this port set out to escape, one level up. It
/// was verified live instead: pointed at a bare nuget lib folder the PowerShell printed its
/// warning and then threw, while this returns 74 types loaded, 11 unloadable, 1 member dropped.
/// </remarks>
public class AssemblyApiTests
{
    private static string Assembly => typeof(SurfaceProbe).Assembly.Location;

    private static string Folder => Path.GetDirectoryName(Assembly)!;

    private static AssemblyApiResult Probe(AssemblyApiRequest? request = null) =>
        AssemblyApi.Describe(Assembly, Folder, request ?? new AssemblyApiRequest { Type = "^I?SurfaceProbe" });

    private static AssemblyType Type(AssemblyApiResult result, string name) =>
        result.Types.Single(t => t.Name == name);

    [Fact]
    public void EachKindIsNamed()
    {
        AssemblyApiResult result = Probe();

        Assert.Equal("interface", Type(result, "ISurfaceProbe").Kind);
        Assert.Equal("enum", Type(result, "SurfaceProbeMode").Kind);
        Assert.Equal("abstract class", Type(result, "SurfaceProbeBase").Kind);
        Assert.Equal("class", Type(result, "SurfaceProbe").Kind);
    }

    [Fact]
    public void TheBaseTypeIsReportedByItsShortName()
    {
        Assert.Equal("SurfaceProbeBase", Type(Probe(), "SurfaceProbe").BaseType);
    }

    [Fact]
    public void InheritedMembersAreExcludedByDefault()
    {
        IReadOnlyList<AssemblyMember> members = Type(Probe(), "SurfaceProbe").Members;

        Assert.DoesNotContain(members, m => m.Name == "InheritedCount");
        Assert.DoesNotContain(members, m => m.Name == "ToString");
    }

    [Fact]
    public void InheritedBringsThemBack()
    {
        AssemblyApiResult result = Probe(new AssemblyApiRequest { Type = "^SurfaceProbe$", Inherited = true });
        IReadOnlyList<AssemblyMember> members = Type(result, "SurfaceProbe").Members;

        Assert.Contains(members, m => m.Name == "InheritedCount");
        Assert.Contains(members, m => m.Name == "ToString");
    }

    [Fact]
    public void StaticMembersAreExcludedByDefault()
    {
        IReadOnlyList<AssemblyMember> members = Type(Probe(), "SurfaceProbe").Members;

        Assert.DoesNotContain(members, m => m.Name == "Create");
        Assert.DoesNotContain(members, m => m.Name == "StaticField");
    }

    [Fact]
    public void StaticBringsThemBack()
    {
        AssemblyApiResult result = Probe(new AssemblyApiRequest { Type = "^SurfaceProbe$", Static = true });
        IReadOnlyList<AssemblyMember> members = Type(result, "SurfaceProbe").Members;

        Assert.Contains(members, m => m.Name == "Create");
        Assert.Contains(members, m => m.Name == "StaticField");
    }

    [Fact]
    public void ConstructorsAndCompilerSpellingsAreLeftOut()
    {
        AssemblyApiResult result = Probe(new AssemblyApiRequest { Type = "^SurfaceProbe$", Static = true });
        IReadOnlyList<AssemblyMember> members = Type(result, "SurfaceProbe").Members;

        Assert.DoesNotContain(members, m => m.Kind == "Constructor");

        // get_Name / set_Name / add_Fired / remove_Fired / op_Addition all exist on this type and
        // are all already represented by the property, the event, and nothing respectively.
        // Listing both doubles the output and buries the real surface in accessor noise.
        Assert.DoesNotContain(members, m => m.Name.StartsWith("get_", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Name.StartsWith("set_", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Name.StartsWith("add_", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Name.StartsWith("op_", StringComparison.Ordinal));

        Assert.Contains(members, m => m is { Kind: "Property", Name: "Name" });
        Assert.Contains(members, m => m is { Kind: "Event", Name: "Fired" });
    }

    [Fact]
    public void EachMemberCarriesTheTypeItActuallyIs()
    {
        IReadOnlyList<AssemblyMember> members = Type(Probe(), "SurfaceProbe").Members;

        Assert.Equal("String", members.Single(m => m.Name == "Name").Type);
        Assert.Equal("Int32", members.Single(m => m.Name == "Field").Type);
        Assert.Equal("Void", members.Single(m => m.Name == "Ping").Type);

        // An event's type is deliberately blank: the switch names property, field and return
        // types, and anything else has none to report rather than a guess.
        Assert.Equal("", members.Single(m => m.Name == "Fired").Type);
    }

    [Fact]
    public void MembersAreSortedByKindThenName()
    {
        IReadOnlyList<AssemblyMember> members = Type(Probe(), "SurfaceProbe").Members;

        List<string> expected = [.. members
            .OrderBy(m => m.Kind, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(m => m.Name, StringComparer.InvariantCultureIgnoreCase)
            .Select(m => $"{m.Kind}.{m.Name}")];

        Assert.Equal(expected, [.. members.Select(m => $"{m.Kind}.{m.Name}")]);
    }

    [Fact]
    public void TypesAreSortedByName()
    {
        List<string> names = [.. Probe().Types.Select(t => t.Name)];

        Assert.Equal([.. names.OrderBy(n => n, StringComparer.InvariantCultureIgnoreCase)], names);
    }

    [Fact]
    public void TruncationIsReportedRatherThanImplied()
    {
        AssemblyApiResult result = Probe(new AssemblyApiRequest { Type = "^I?SurfaceProbe", MaxTypes = 2 });

        Assert.Equal(4, result.Matched);
        Assert.Equal(2, result.Returned);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.Types.Count);
    }

    [Fact]
    public void AMemberPatternNarrowsWithinTheMatchedTypes()
    {
        AssemblyApiResult result = Probe(new AssemblyApiRequest { Type = "^SurfaceProbe$", Member = "^Na" });

        Assert.Equal(["Name"], Type(result, "SurfaceProbe").Members.Select(m => m.Name));
    }

    [Fact]
    public void BothPatternsAreCaseInsensitive()
    {
        AssemblyApiResult result = Probe(new AssemblyApiRequest { Type = "^surfaceprobe$", Member = "^na" });

        Assert.Equal(["Name"], Type(result, "SurfaceProbe").Members.Select(m => m.Name));
    }

    [Fact]
    public void AFolderHoldingTheWholeClosureDropsNothing()
    {
        AssemblyApiResult result = Probe();

        Assert.Equal(0, result.TypesUnloadable);
        Assert.Equal(0, result.MembersDropped);
        Assert.Null(result.SiblingWarning);
        Assert.True(result.Siblings > 1, "the test output folder should hold the dependency closure");
    }

    [Fact]
    public void AMissingAssemblyIsNamedRatherThanGuessedAt()
    {
        GraphException ex = Assert.Throws<GraphException>(
            () => AssemblyApi.Describe("NoSuchAssemblyAnywhere", Folder, new AssemblyApiRequest()));

        Assert.Contains("NoSuchAssemblyAnywhere.dll", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitPathIsUsedAsGiven()
    {
        Assert.Equal(Assembly, AssemblyApi.ResolveAssemblyPath(Assembly, "."));
    }

    [Fact]
    public void EveryCallReadsFromDiskAgain()
    {
        // The point of the collectible context. The PowerShell used LoadFrom, which pins the
        // assembly for the life of the process, so a second call in one session answered from
        // the first load -- a rebuilt target returned its OLD surface, and a sibling problem
        // stopped reproducing once anything had loaded the dependency. Two calls agreeing is not
        // proof on its own, but a context that failed to unload would show up here first.
        AssemblyApiResult first = Probe();
        AssemblyApiResult second = Probe();

        Assert.Equal(first.TypesLoaded, second.TypesLoaded);
        Assert.Equal(
            first.Types.Select(t => t.Name),
            second.Types.Select(t => t.Name));
    }
}
