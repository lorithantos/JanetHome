using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// Serializes API-doc answers in the shape Get-ApiDoc.ps1 already emits, field for field and in
/// its order. The envelope is what a caller parses, so it is a contract rather than a formatting
/// choice.
/// </summary>
public static class ApiDocJson
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

    public static string Serialize(ApiDocResult result, bool pretty = false)
    {
        JsonArray members = [];
        foreach (ApiMember member in result.Members)
        {
            members.Add(Member(member));
        }

        JsonObject root = new()
        {
            ["source"] = result.Source,
            ["returned"] = result.Returned,
            ["totalMatches"] = result.TotalMatches,
            ["truncated"] = result.Truncated,
            ["members"] = members,
        };

        return root.ToJsonString(pretty ? Indented : Compact);
    }

    public static string Serialize(ApiDocOrientation orientation, bool pretty = false)
    {
        JsonObject kinds = [];
        foreach ((string kind, int count) in orientation.Kinds)
        {
            kinds[kind] = count;
        }

        JsonObject types = [];
        foreach ((string type, int count) in orientation.Types)
        {
            types[type] = count;
        }

        JsonObject root = new()
        {
            ["source"] = orientation.Source,
            ["total"] = orientation.Total,
            ["kinds"] = kinds,
            ["types"] = types,
        };

        return root.ToJsonString(pretty ? Indented : Compact);
    }

    private static JsonObject Member(ApiMember member)
    {
        JsonArray parameters = [];
        foreach (ApiParameter parameter in member.Parameters)
        {
            parameters.Add(new JsonObject { ["name"] = parameter.Name, ["doc"] = parameter.Doc });
        }

        JsonArray exceptions = [];
        foreach (ApiException exception in member.Exceptions)
        {
            exceptions.Add(new JsonObject { ["type"] = exception.Type, ["doc"] = exception.Doc });
        }

        JsonArray typeParams = [];
        foreach (ApiParameter typeParam in member.TypeParams)
        {
            typeParams.Add(new JsonObject { ["name"] = typeParam.Name, ["doc"] = typeParam.Doc });
        }

        return new JsonObject
        {
            ["id"] = member.Id,
            ["kind"] = member.Kind,
            ["name"] = member.Name,
            ["declaring"] = member.Declaring,
            ["signature"] = member.Signature,
            ["summary"] = member.Summary,
            ["returns"] = member.Returns,
            ["value"] = member.Value,
            ["parameters"] = parameters,
            ["inheritdoc"] = member.Inheritdoc,
            ["remarks"] = member.Remarks,
            ["exceptions"] = exceptions,
            ["typeparams"] = typeParams,
        };
    }

    // ---- the reading view -------------------------------------------------------------------

    public static string Render(ApiDocOrientation orientation)
    {
        StringBuilder text = new();
        text.AppendLine();
        text.AppendLine($"{orientation.Source} -- {orientation.Total} documented members");

        foreach ((string kind, int count) in orientation.Kinds)
        {
            text.AppendLine($"  {kind,-9} {count}");
        }

        text.AppendLine();
        text.AppendLine("LARGEST TYPES");

        foreach ((string type, int count) in orientation.Types)
        {
            text.AppendLine($"  {count,-5} {type}");
        }

        text.AppendLine();
        text.AppendLine("Query with -Query <text>, -Type <type>, -Id <member>, or -Kind <kind>.");
        text.AppendLine();

        return text.ToString();
    }

    public static string Render(ApiDocResult result, bool full)
    {
        if (result.Members.Count == 0)
        {
            return "No matching members. Run with -Package alone for kinds and the largest types." + Environment.NewLine;
        }

        StringBuilder text = new();
        text.AppendLine();

        foreach (ApiMember member in result.Members)
        {
            // A type's declaring name is already its full name, so joining would print the last
            // segment twice.
            text.AppendLine(member.Kind == "Type" ? member.Declaring : $"{member.Declaring}.{member.Signature}");
            text.AppendLine(string.IsNullOrEmpty(member.Summary) ? $"  [{member.Kind}] (undocumented)" : $"  [{member.Kind}] {member.Summary}");

            foreach (ApiParameter parameter in member.Parameters)
            {
                text.AppendLine($"  {parameter.Name}: {parameter.Doc}");
            }

            if (!string.IsNullOrEmpty(member.Returns))
            {
                text.AppendLine($"  returns: {member.Returns}");
            }

            if (!string.IsNullOrEmpty(member.Value))
            {
                text.AppendLine($"  value:   {member.Value}");
            }

            if (!string.IsNullOrEmpty(member.Inheritdoc))
            {
                text.AppendLine($"  {member.Inheritdoc}");
            }

            if (full)
            {
                if (!string.IsNullOrEmpty(member.Remarks))
                {
                    text.AppendLine($"  remarks: {member.Remarks}");
                }

                foreach (ApiException exception in member.Exceptions)
                {
                    text.AppendLine($"  throws {exception.Type}: {exception.Doc}");
                }

                foreach (ApiParameter typeParam in member.TypeParams)
                {
                    text.AppendLine($"  <{typeParam.Name}>: {typeParam.Doc}");
                }
            }

            text.AppendLine();
        }

        text.AppendLine(result.Truncated
            ? $"top {result.Returned} of {result.TotalMatches} matches. -First N for more, -All for every match."
            : $"{result.Returned} member{(result.Returned != 1 ? "s" : "")}");

        text.AppendLine();

        return text.ToString();
    }
}
