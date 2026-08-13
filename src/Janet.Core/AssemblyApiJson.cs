using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// Serializes the assembly surface in the shape Get-AssemblyApi.ps1 already emits, plus one
/// declared addition.
/// </summary>
public static class AssemblyApiJson
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions Indented = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string Serialize(AssemblyApiResult result, bool pretty = true)
    {
        JsonArray types = [];
        foreach (AssemblyType type in result.Types)
        {
            JsonArray members = [];
            foreach (AssemblyMember member in type.Members)
            {
                members.Add(new JsonObject
                {
                    ["kind"] = member.Kind,
                    ["type"] = member.Type,
                    ["name"] = member.Name,
                });
            }

            types.Add(new JsonObject
            {
                ["name"] = type.Name,
                ["kind"] = type.Kind,
                ["baseType"] = type.BaseType,
                ["members"] = members,
            });
        }

        JsonObject root = new()
        {
            ["contract"] = result.Contract,
            ["assembly"] = result.Assembly,
            ["folder"] = result.Folder,
            ["siblings"] = result.Siblings,
            ["typesLoaded"] = result.TypesLoaded,
            ["typesUnloadable"] = result.TypesUnloadable,
            ["matched"] = result.Matched,
            ["returned"] = result.Returned,
            ["truncated"] = result.Truncated,
            ["types"] = types,
        };

        // Two declared additions, both about the same failure.
        //
        // siblingWarning: the PowerShell raised this on the warning stream, which an MCP client
        // never sees and a redirected caller drops, so the diagnosis for the most common failure
        // ("you pointed at a nuget lib folder") was reaching nobody. Present only when there is
        // something to say, so its presence is the signal.
        //
        // membersDropped: how many members were left out because a type in their signature could
        // not be resolved. Always present, because 0 is the answer worth being able to see -- an
        // absent field would make "nothing was dropped" and "this build does not report it"
        // look the same.
        if (result.SiblingWarning is not null)
        {
            root["siblingWarning"] = result.SiblingWarning;
        }

        root["membersDropped"] = result.MembersDropped;

        return root.ToJsonString(pretty ? Indented : Compact);
    }

    public static string Render(AssemblyApiResult result)
    {
        StringBuilder text = new();

        string cap = result.Truncated ? $", showing {result.Returned}" : "";
        text.AppendLine($"{result.Assembly} -- {result.TypesLoaded} types loaded, {result.Matched} matched{cap}");

        if (result.TypesUnloadable > 0)
        {
            text.AppendLine($"  ({result.TypesUnloadable} type(s) could not be loaded; {result.Siblings} assemblies in the folder)");
        }

        if (result.MembersDropped > 0)
        {
            text.AppendLine($"  ({result.MembersDropped} member(s) dropped: a type in the signature could not be resolved)");
        }

        if (result.SiblingWarning is not null)
        {
            text.AppendLine($"  {result.SiblingWarning}");
        }

        foreach (AssemblyType type in result.Types)
        {
            text.AppendLine();
            text.AppendLine($"{type.Kind} {type.Name}{(type.BaseType is null ? "" : $" : {type.BaseType}")}");

            foreach (AssemblyMember member in type.Members)
            {
                text.AppendLine($"    {member.Kind.PadRight(8)} {member.Type} {member.Name}");
            }
        }

        return text.ToString();
    }
}
