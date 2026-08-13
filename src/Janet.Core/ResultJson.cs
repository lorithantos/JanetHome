using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>Serializes write results in the shape the PowerShell scripts already emit.</summary>
public static class ResultJson
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

    public static string Serialize(AddResult result, bool pretty = false)
    {
        JsonObject root = new()
        {
            ["added"] = result.Added,
            ["dryRun"] = result.DryRun,
            ["id"] = result.Id,
        };

        if (result.DryRun)
        {
            root["warnings"] = Strings(result.Warnings);
            root["nodeText"] = result.NodeText;
            root["reverseLinks"] = Strings(result.ReverseLinks);
        }
        else
        {
            root["totalNodes"] = result.TotalNodes;
            root["warnings"] = Strings(result.Warnings);
            root["reverseLinks"] = Strings(result.ReverseLinks);
        }

        return root.ToJsonString(pretty ? Indented : Compact);
    }

    public static string Serialize(UpdateResult result, bool pretty = false)
    {
        JsonArray changes = [];
        foreach (FieldChange change in result.Changes)
        {
            changes.Add(new JsonObject
            {
                ["field"] = change.Field,
                ["from"] = change.From,
                ["to"] = change.To,
            });
        }

        JsonObject root = new()
        {
            ["updated"] = result.Updated,
            ["dryRun"] = result.DryRun,
            ["id"] = result.Id,
            ["changes"] = changes,
            ["warnings"] = Strings(result.Warnings),
            ["totalNodes"] = result.TotalNodes,
        };

        if (result.NodeText is not null)
        {
            root["nodeText"] = result.NodeText;
        }

        return root.ToJsonString(pretty ? Indented : Compact);
    }

    public static string Serialize(RenameResult result, bool pretty = false)
    {
        JsonObject root = new()
        {
            ["renamed"] = result.Renamed,
            ["dryRun"] = result.DryRun,
            ["id"] = result.Id,
            ["newId"] = result.NewId,
            ["relinked"] = Strings(result.Relinked),
            // Named separately from relinked because they are the opposite thing: relinked is
            // what this operation fixed, bodyReferences is what it deliberately did not.
            ["bodyReferences"] = Strings(result.BodyReferences),
            ["warnings"] = Strings(result.Warnings),
            ["totalNodes"] = result.TotalNodes,
        };

        return root.ToJsonString(pretty ? Indented : Compact);
    }

    private static JsonArray Strings(IReadOnlyList<string> values)
    {
        JsonArray array = [];
        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
