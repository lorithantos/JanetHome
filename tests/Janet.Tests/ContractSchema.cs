using System.Text.Json.Nodes;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Whole-schema checks over a contract under contracts\, shared by the classes that hold an
/// envelope to its schema.
/// </summary>
/// <remarks>
/// The section-by-section tests each pin the objects someone thought to name, which is how the
/// dotnet-check schema came to have build, tests, assemblies[] and slowest[] pinned and six
/// siblings not: a partially pinned envelope looks covered, and that is what stops anyone
/// looking. These walk the whole document instead, so an object nobody named is still checked.
/// </remarks>
internal static class ContractSchema
{
    public static JsonObject Load(string file) =>
        JsonNode.Parse(File.ReadAllText(Fixture.Resolve("Contracts", file)))!.AsObject();

    public static IEnumerable<string> Declared(JsonObject schema) =>
        schema["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);

    public static IEnumerable<string> Required(JsonObject schema) =>
        (schema["required"]?.AsArray() ?? []).Select(r => r!.GetValue<string>()).Order(StringComparer.Ordinal);

    /// <summary>
    /// Every object the schema declares refuses a key it does not name, and requires every key it
    /// does. The failure names the file and the JSON path of the open object.
    /// </summary>
    public static void AssertEveryObjectIsClosedAndRequiresEveryProperty(string file) =>
        AssertEveryObjectIsClosedAndRequiresEveryProperty(file, []);

    /// <summary>
    /// The same, for a contract that declares keys which are present only when there is
    /// something to say -- assembly-api's siblingWarning, az-token's token.
    /// </summary>
    /// <param name="file">The schema under contracts\.</param>
    /// <param name="optional">
    /// Each key allowed to be left out of 'required', as its object's path and the key name,
    /// "#/siblingWarning" for a root key. Every entry must name a declared property, so a list
    /// that outlives the key it excused fails rather than excusing nothing.
    /// </param>
    public static void AssertEveryObjectIsClosedAndRequiresEveryProperty(string file, IReadOnlyCollection<string> optional)
    {
        HashSet<string> excused = [];

        foreach ((string path, JsonObject schema) in ObjectSchemas(Load(file), "#"))
        {
            Assert.True(schema["additionalProperties"]?.GetValue<bool>() == false, $"{file} {path}: additionalProperties is not false");

            IEnumerable<string> mustRequire = Declared(schema).Where(key => !optional.Contains($"{path}/{key}"));
            Assert.True(mustRequire.SequenceEqual(Required(schema)),
                $"{file} {path}: required is not every declared property");

            excused.UnionWith(Declared(schema).Select(key => $"{path}/{key}").Where(optional.Contains));
        }

        string[] stale = optional.Where(entry => !excused.Contains(entry)).ToArray();
        Assert.True(stale.Length == 0, $"{file}: optional names no declared property: {string.Join(", ", stale)}");
    }

    /// <summary>Every node in the document that declares properties, with a readable path to it.</summary>
    public static IEnumerable<(string Path, JsonObject Schema)> ObjectSchemas(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["properties"] is JsonObject)
                {
                    yield return (path, obj);
                }

                foreach ((string key, JsonNode? child) in obj)
                {
                    foreach ((string Path, JsonObject Schema) found in ObjectSchemas(child, $"{path}/{key}"))
                    {
                        yield return found;
                    }
                }

                break;

            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    foreach ((string Path, JsonObject Schema) found in ObjectSchemas(array[i], $"{path}/{i}"))
                    {
                        yield return found;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Walks an emitted envelope beside its schema: every emitted object must carry every key its
    /// schema object requires and nothing it does not declare, at every depth, and each schema
    /// object the walk reaches is recorded in <paramref name="reached"/>.
    /// </summary>
    /// <remarks>
    /// For a schema whose objects require every key they declare -- which the closed check above
    /// enforces wherever no key is named optional -- that is an exact key-set comparison. A key
    /// the schema declares but does not require may be absent, so an envelope that leaves out a
    /// present-only-when-there-is-something-to-say key still matches; a test that wants such a
    /// key sampled has to emit an envelope that carries it.
    /// <para>
    /// A schema object is found through a local $ref, through the one non-null arm of a nullable
    /// oneOf, or -- for a tagged union -- through the arm whose 'status' const is the emitted
    /// status. Comparing <paramref name="reached"/> with <see cref="ObjectSchemas"/> afterwards
    /// says whether the sample was populated enough to reach every object, which is the thing a
    /// cheap live sample cannot promise: a subschema only applies to data that exists.
    /// </para>
    /// </remarks>
    public static void AssertEmittedMatches(JsonObject root, JsonNode schemaNode, JsonNode emitted, string where, ISet<JsonObject> reached)
    {
        JsonObject schema = Resolve(root, schemaNode, emitted);

        if (schema["properties"] is not JsonObject properties)
        {
            return;
        }

        reached.Add(schema);
        IEnumerable<string> keys = emitted.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);
        Assert.True(
            !Required(schema).Except(keys).Any() && !keys.Except(Declared(schema)).Any(),
            $"{where}: declared [{string.Join(", ", Declared(schema))}], required [{string.Join(", ", Required(schema))}] but emitted [{string.Join(", ", keys)}]");

        foreach ((string name, JsonNode? value) in emitted.AsObject())
        {
            switch (value)
            {
                case JsonObject:
                    AssertEmittedMatches(root, properties[name]!, value, $"{where}.{name}", reached);
                    break;

                case JsonArray items:
                    foreach (JsonNode? item in items)
                    {
                        if (item is JsonObject)
                        {
                            JsonObject arraySchema = Resolve(root, properties[name]!, items);
                            AssertEmittedMatches(root, arraySchema["items"]!, item, $"{where}.{name}[]", reached);
                        }
                    }

                    break;
            }
        }
    }

    private static JsonObject Resolve(JsonObject root, JsonNode node, JsonNode emitted)
    {
        JsonObject schema = node.AsObject();

        if (schema["$ref"]?.GetValue<string>() is string reference)
        {
            Assert.StartsWith("#/definitions/", reference);
            return Resolve(root, root["definitions"]![reference["#/definitions/".Length..]]!, emitted);
        }

        if (schema["oneOf"] is JsonArray arms)
        {
            JsonNode[] candidates = arms.Where(arm => arm!["type"]?.GetValue<string>() != "null").Select(arm => arm!).ToArray();

            if (candidates.Length > 1 && emitted is JsonObject obj && obj["status"]?.GetValue<string>() is string status)
            {
                candidates = candidates.Where(arm => arm["properties"]?["status"]?["const"]?.GetValue<string>() == status).ToArray();
            }

            Assert.True(candidates.Length == 1, $"{candidates.Length} oneOf arms match the emitted value");
            return Resolve(root, candidates[0], emitted);
        }

        return schema;
    }
}
