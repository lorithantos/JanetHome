# Storage copy: refresh and clear a temporary staging copy of one client's live data

Status: design approved in conversation 2026-10-31; written spec approved by the owner the
same day. Plan: `docs\superpowers\plans\2026-10-31-storage-copy.md`.
Thread item: "Janet tool to refresh and clear a staging copy of a client's live data across
storage accounts" (area JanetHome).

## 1. Purpose and agreed understanding

The owner wants staging tests on the neelam-aesthetics staging app to run against data that
looks like the real thing: Priya's data from "her site". The copy is TEMPORARY. Priya knows
her data is being used; it is cleared once the tests are done. Nothing is ever copied back,
and Priya does not see staging.

Today no Janet tool copies blobs or table entities between storage accounts. `storage_probe`
only reads, and the house rule is to build the tool rather than hand-type `az` commands.

Success means:

- one read-only call shows exactly what a refresh (or clear) would delete and copy;
- one confirmed call performs it, and only what was shown;
- one confirmed call clears the copy afterwards;
- no call can ever write to the account that holds her real data.

### Recorded decision this supersedes

neelam `WIP.md:760` (staging seeding) says "Nothing is copied from her account: staging
holds public sample content only." The owner's request of 2026-10-31 supersedes it. When
the tool lands, that entry is rewritten as an owner-approved, temporary exception: a copy
made with Priya's knowledge, live data only, refreshed before testing and cleared after.
`test.bicepparam:1` and `main.bicep:10-11` ("made-up clients, no client data") are already
stale and are corrected in the same neelam change. neelam's rule "A deleted save's contents
leave no record" (CLAUDE.md:68-72) is why only LIVE data is copied even into a temporary
copy.

## 2. Decisions

| Decision | Choice | Why |
|---|---|---|
| Where the storage layout is known | A JSON spec checked into neelam; Janet holds a generic engine | Janet stays domain-agnostic; neelam owns the facts about its own storage and pins them with its own tests |
| Production / real-data guard | Allow-list of named source/target pairs in the spec | Fails closed: an account nobody listed as a target can never be written |
| Copy mechanism | Server-side Put Blob From URL; tables read, rewritten, upserted | Photos never transit this machine; AzureHttp needs no binary body path |
| Refresh semantics | Wipe the target client's in-scope data, then copy | Anything deleted at the source is gone from the target after a refresh; re-running is the recovery |
| Tool surface | `storage_copy_plan` (read-only) + `storage_copy_apply` (writes, requires the plan's hash) | Matches azure_whatif/azure_deploy; the dry run can be allow-listed while every write is prompted |
| Mid-run failure | Stop at the first failed operation, report partial counts | A run that "mostly worked" invites trusting an unverified target |

Rejected: neelam-specific rules compiled into Janet (drifts silently when neelam changes);
neelam emitting a plan from its own code (needs a new neelam CLI that reads storage);
download-and-upload through this machine; copy-then-prune sync (more logic, no payoff at
neelam's volume); a deny-list guard (fails open); a tag check on the target (extra deploy,
can be added later if an edited spec ever worries us).

## 3. The neelam layout this has to serve (survey, 2026-10-31)

Paths relative to `C:\repos\neelam-aesthetics`.

- One blob container per client, named by the client (`ClientName.cs:5-12`,
  `ClientStores.cs:81-104`). Her client is `test-salon-one` on account
  `neelamtest6excvnu62r4z6` (site `neelamtest-6excvnu62r4z6`, RG `neelam-test-rg`).
  Staging's client is `staging-salon` on `neelamteststg6excvnu62r4`.
- In the client container: `drafts/{id:N}/{stamp}.json`, `templates/{id:N}/{stamp}.json`,
  `proofread/{id:N}/{stamp}.json`, `proofread/images/{sha256}.json`, `catalog/`, `policy/`,
  `baseline/`, `ladders/` (each `{stamp}.json`), `images/{escaped name}`.
- The client's look lives in the shared `settings` container under `settings/{client}/`.
  The same container holds operator data (`_standard-baseline/`, `_standard-ladders/`,
  `_starts/`) that belongs to no client and is never touched.
- Tables: `knownItems` (PK client, RK item id), `approvals` (PK client, RK
  `{id:N}_{stamp}`), `activity`, `clients`, `supportGrants`. Only `knownItems` and
  `approvals` are copied.
- Dead marker: blob metadata `undone` (`UndoMark.cs:14-25`). A blob is live when the key is
  absent. Proofreads and approval rows have no marker of their own; they are live only when
  `drafts/{id}/{stamp}.json` is live. Known items and photos are deleted outright, so all
  present are live.
- The app reads blob name, metadata and ETag (`AzureBlobBackend.cs:33-37`): a copy keeps
  name, metadata and content type.
- Auth is Entra only (`allowSharedKeyAccess: false`); versioning and soft delete are off, so
  a wipe on the target is final. The developer holds Blob and Table Data Contributor on both
  test accounts (`storage.bicep:165-183`).
- Staging's managed identity is scoped per container and per table, so the copy must RENAME
  the client: container, `settings/` prefix, and table PartitionKey.

## 4. The spec format (lives in neelam)

```json
{
  "pairs": [
    { "name": "priya-to-staging",
      "source": { "account": "neelamtest6excvnu62r4z6", "client": "test-salon-one" },
      "target": { "account": "neelamteststg6excvnu62r4", "client": "staging-salon" } }
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
    { "name": "settings", "prefix": "settings/{client}/",
      "rules": [ { "match": "settings/{client}/{stamp}.json", "dropIfMetadata": "undone" } ] }
  ],
  "tables": [
    { "name": "knownItems", "partitionKey": "{client}" },
    { "name": "approvals",  "partitionKey": "{client}", "rowKey": "{id}_{stamp}",
      "keepIfLive": { "container": "{client}", "blob": "drafts/{id}/{stamp}.json" } }
  ]
}
```

Semantics:

- `{client}` is the only reserved placeholder: the source client when reading, the target
  client when writing. That substitution IS the rename.
- Every other placeholder captures exactly one path segment (no `/`). In a `rowKey`
  pattern, placeholders are separated by the literal text between them.
- Within a container, rules are tried in order and the first match wins.
- `dropIfMetadata: "<key>"` drops a blob whose metadata carries that key.
- `keepIfLive` keeps an item only if the named blob exists at the SOURCE and is itself live
  under its own rule.
- `"copy": false` on a rule excludes matching blobs deliberately (they are neither copied
  nor wiped).
- A container's SCOPE is its `prefix`, or the whole container when there is none. Every
  source blob in scope must match a rule; one that matches none is `unmatched` and blocks
  apply. Only target blobs in scope and matched by a copying rule are wiped.
- Blob content is copied byte for byte, never rewritten.

The spec is refused (an exception, nothing attempted) when: it fails its JSON schema
(unknown keys included); a `keepIfLive` uses a placeholder its `match`/`rowKey` did not
capture; a `keepIfLive` names a blob no rule covers; a pair name repeats.

## 5. Guards (checked before any request is sent, in both tools)

1. `pair` names a pair in the spec.
2. The pair's target account is listed as a target.
3. No account appears as a source in any pair and as a target in any pair.
4. Source and target are not the same account and client.

A failed guard throws. Because her account is a source, guard 3 means no edit that adds a
reverse pair can make it writable without also removing it as a source.

## 6. Tools

Both are MCP-only, registered in `src\Janet.Mcp\AzureTools.cs`, with the engine in
`src\Janet.Core` (a new `StorageCopy.cs`; the data-plane calls it needs live beside it, not
in `AzureHttp`).

### `storage_copy_plan` -- READ-ONLY

Parameters: `spec` (path), `pair`, `mode` (`refresh` | `clear`), `rule` (optional: list
names for that rule only), `subscription`, `resourceGroup` (optional, as storage_probe).

Reads both accounts (blob list by prefix with paging, metadata included; table query by
PartitionKey with continuation) and returns, contract 1,
`contracts\storage-copy-plan.schema.json`:

- `mode`, `pair`, `spec`, `source` {account, client}, `target` {account, client}
- `delete`: {blobs, entities, byRule[]}
- `copy`: {blobs, bytes, entities, byRule[]} (empty in clear mode)
- `dropped`: {count, byReason[]} where reason is `undone` or `noLiveSave`
- `unmatched`: {count, names[]}
- `names`: null unless `rule` is given; then that rule's delete and copy names
- `readyToApply` and `blockers[]`: unmatched source blobs; a target container or table that
  does not exist (Janet never creates them; they belong to Bicep and its role scopes)
- `planHash`: SHA-256 over the sorted operation list plus every item's ETag on both sides

Blob names are safe to return (neelam CLAUDE.md: customer data does not live in blob
names). Metadata VALUES are never returned: `title` carries a campaign title.

### `storage_copy_apply` -- CHANGES LIVE AZURE

Parameters: as the plan minus `rule`, plus a required `planHash`.

Recomputes the plan. Hash mismatch -> outcome `stale`, nothing written. Blockers -> outcome
`blocked`, nothing written. Otherwise: delete the target's in-scope matched blobs and the
target client's entities in each listed table, then copy.

- Blob: Put Blob From URL on the target with `x-ms-copy-source-authorization: Bearer
  <storage token>`, `x-ms-blob-content-type`, and every source metadata pair as
  `x-ms-meta-*` (a from-URL copy does not carry metadata). Blob API 2023-11-03.
- Entity: query with `Accept: application/json;odata=minimalmetadata` so `@odata.type`
  annotations survive; strip `odata.etag` and `Timestamp`; set the target PartitionKey;
  Insert Or Replace. Delete by query on the target PartitionKey, `If-Match: *`. Table API
  2019-02-02 (Entra needs 2017-11-09 or later). No batching.

Returns, contract 1, `contracts\storage-copy-apply.schema.json`: `outcome` (`applied` |
`cleared` | `stale` | `blocked` | `failed`), `planHash`, `deleted` {blobs, entities},
`copied` {blobs, bytes, entities}, `failure` (null, or {item, operation, error} with ARM's
nested error via `AzureResponse.Error()`), `copiedAt`, and `clear` (the exact
`storage_copy_apply` call, mode clear, that removes a refresh; the plan must be taken first).

Errors: only a request that cannot be attempted throws (guard, spec, no `az login`).
Everything else is a field. A `429` or `503` is retried in the copy loop only, honouring
`Retry-After`, three tries, each wait clamped to 30 s; then it is the failure.

Both tools get a `Narrowing.Hints` row (plan: "pass rule=<rule> to list that rule's
names"); apply is bounded by construction.

### Life cycle

A refresh's envelope names `copiedAt` and the clear call; the caller adds a neelam thread
item ("staging holds Priya's data since <date> -- clear when tests are done") so every
neelam startup brief shows the copy until it is cleared. No marker is written into the
target account.

Operating rule for the neelam README: do not refresh while anyone is testing on staging --
the staging app writes to the same container and a refresh wipes it.

Known effect: `ApprovalRecord.SeenAcross` unions warning keys over every approval row of a
campaign; dropped dead rows mean previously seen warnings can reappear on staging. Testers
only; acceptable.

## 7. Testing

Unit tests (`tests\Janet.Tests\StorageCopyTests.cs`), no live Azure, driving `StubAzure`
extended to record request headers:

- Source is never written: across a refresh and a clear, every non-GET request's host is
  the target account. Mutation: point one PUT at the source; the test must fail.
- Each guard refuses with zero requests recorded.
- Rules: first match wins (`proofread/images/` before `proofread/{id}/`); `dropIfMetadata`;
  `keepIfLive` with a live, an undone and a missing parent; spec refusals.
- Unmatched source blob -> blocker; apply `blocked`, zero writes.
- ETag change between plan and apply -> `stale`, zero writes.
- Copy fidelity: copy-source authorization, every `x-ms-meta-*`, content type; PartitionKey
  rewritten, typed properties round-trip, `Timestamp`/ETag stripped.
- Wipe scope: a target blob outside `settings/{client}/` is never deleted.
- Failure: stop at first error with partial counts; a `429` is retried then succeeds.

House gates: schema key-set, description and contract-version theories in
`AzureSchemaTests`; `ToolSurfaceTests` narrowing rows; a contract-gate sampler for the plan
only, skipped unless an env var names spec and pair (write tools are never sampled). All
through `janet check`. Every added test is mutation-checked.

neelam (its own repo and commit): `storage-copy.json` plus a test pinning it against
`CampaignStore` prefixes, `TableMetadata` table names and `UndoMark`'s key; the WIP.md,
README and Bicep-comment corrections from section 1.

Live proof, each step with the owner's go-ahead: plan against the real pair (read-only);
owner reviews counts; apply with that hash (prompted); staging smoke check; clear when tests
are done.

## 8. Open check for planning -- settled 2026-10-31

Question: does draft or template JSON embed the client name? If it did, staging content would
carry `test-salon-one`.

Answer: no. `CampaignStore.SaveDraftAsync` serializes through
`CampaignJson.SerializeDraft(CampaignDraft)`, which takes only the draft, and `CampaignDraft`
carries Blocks, Label, Preheader, Subject, TemplateId, TemplateName and TemplateVersion;
`CampaignTemplate` carries Name and Blocks (graph `neelam`, built 2026-10-31 20:40Z). The
client exists only as the container name, so the rename in section 4 is complete. A salon name
typed into a block's text is her content and is copied as written.
