namespace Janet.Tests;

internal static class StorageCopyFixtures
{
    /// <summary>Spec section 4 of the storage copy design, with the accounts shortened to src and tgt.</summary>
    internal const string Neelam = """
        {
          "pairs": [
            { "name": "priya-to-staging",
              "source": { "account": "src", "client": "test-salon-one" },
              "target": { "account": "tgt", "client": "staging-salon" } }
          ],
          "containers": [
            { "name": "{client}",
              "rules": [
                { "match": "drafts/{id}/{stamp}.json",     "dropIfMetadata": "undone" },
                { "match": "templates/{id}/{stamp}.json",  "dropIfMetadata": "undone" },
                { "match": "proofread/images/{hash}.json" },
                { "match": "proofread/{id}/{stamp}.json",  "keepIfLive": "drafts/{id}/{stamp}.json" },
                { "match": "catalog/{stamp}.json",         "dropIfMetadata": "undone" },
                { "match": "policy/{stamp}.json",          "dropIfMetadata": "undone" },
                { "match": "baseline/{stamp}.json",        "dropIfMetadata": "undone" },
                { "match": "ladders/{stamp}.json",         "dropIfMetadata": "undone" },
                { "match": "images/{name}" }
              ] },
            { "name": "settings", "prefix": "{client}/",
              "rules": [ { "match": "{client}/{stamp}.json", "dropIfMetadata": "undone" } ] }
          ],
          "tables": [
            { "name": "knownItems", "partitionKey": "{client}" },
            { "name": "approvals",  "partitionKey": "{client}", "rowKey": "{id}_{stamp}",
              "keepIfLive": { "container": "{client}", "blob": "drafts/{id}/{stamp}.json" } }
          ]
        }
        """;
}
