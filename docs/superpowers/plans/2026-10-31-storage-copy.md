# Storage Copy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two Janet MCP tools, `storage_copy_plan` (read-only) and `storage_copy_apply` (writes,
gated by the plan's hash), that refresh and clear a temporary staging copy of one client's live
data from a neelam-checked-in spec, and can never write to the source account.

**Architecture:** A generic engine in `src\Janet.Core`: a spec parser with placeholder patterns
and the four guards, a thin storage data-plane client over `AzureHttp`, and `StorageCopy`, which
inventories both accounts into a sorted operation list (the plan, hashed) and replays it (the
apply). The MCP layer only parses arguments and serializes. neelam owns `infra\storage-copy.json`
and a test pinning it to its own storage constants.

**Tech Stack:** .NET 10, C#, System.Text.Json.Nodes, System.Xml.Linq, xUnit, Azure Blob REST
2023-11-03 and Table REST 2019-02-02 with Entra bearer tokens, PowerShell 7 (contract gate).

**Spec:** `docs\superpowers\specs\2026-10-31-storage-copy-design.md` (approved 2026-10-31).
Executors read it alongside this plan; section numbers below refer to it.

## Global Constraints

- Nothing is ever written to a source account. Every non-GET request goes to the target host.
- Auth is `AzureHttp` with scope `"storage"` (data plane) and `"arm"` (account lookup) only. No
  account keys, SAS or connection strings, anywhere.
- Blob API `StorageProbe.BlobApi` ("2023-11-03"); Table API `StorageProbe.TableApi` ("2019-02-02").
- Only a request that cannot be attempted throws `GraphException`: a spec that fails to parse, a
  failed guard, a missing `planHash`, no `az login`, and a data-plane READ refused with anything
  but 404 (the caller's problem to fix, as `AzureHttp.ArmJson` treats reads). Everything else is
  an envelope field.
- Envelopes: contract 1, every key always present, null rather than absent; schemas draft-07,
  every object closed (`additionalProperties: false`), every property described and required.
- Blob metadata VALUES are never returned in any envelope. Blob names may be.
- MCP only; no CLI verb. Synchronous, like the rest of `Janet.Core`.
- No new package dependencies. The spec's "fails its JSON schema" (spec section 4) is enforced
  by the hand-written parser, which refuses unknown keys at every level; there is no spec schema
  file and no JSON Schema validator.
- The `spec` argument must be an absolute path; a relative one is refused (the MCP server's
  working directory is not the caller's).
- JanetHome: ASCII only (pre-commit enforces); change `.cs` and `.ps1` with Edit/Write, never a
  shell write; build and test with `janet check --target tests\Janet.Tests` (or `dotnet_check`),
  never bare `dotnet test`/`dotnet build`; `pwsh scripts\Test-PowerShellRules.ps1 -Path <file>`
  on every `.ps1` touched; catalog changes through `research_update`, never by hand.
- Every added test is mutation-checked: break the subject, see it fail, restore. The commit body
  names the mutation.
- Commits are part of this approved plan, one per task, in the house voice (checking-in skill):
  state what is true now, no prefixes, a verification line, the harness's attribution trailer.

## Review Focus

1. **Blob names that need escaping** (`images/Spring Offer #1.png`, non-ASCII, `%`): the copy
   must land under the identical name, with each path segment escaped in both the target URL and
   `x-ms-copy-source`, and `/` kept. Pinned in Task 2.
2. **Table keys that need escaping** (a RowKey containing `'`): the entity URL must double the
   quote and percent-encode, and the entity must round-trip. Pinned in Task 2.
3. **More than one page** of blob listing (`NextMarker`) or entity query (continuation headers):
   every page is read, so nothing is silently left uncopied or unwiped. Pinned in Task 2.
4. **A refresh from a source with no live data**: the plan shows only deletes, apply wipes the
   target and copies nothing, outcome `applied`. Pinned in Task 3.
5. **A container or table missing on either side**: a blocker naming it, `readyToApply` false,
   apply `blocked` with zero writes, never an exception. Pinned in Task 3.

Also watch on the live run (Task 7), since no stub can show it: a PUT with no body must reach
Azure with `Content-Length: 0` (Azure answers 411 otherwise).

---

### Task 1: Spec parser, placeholder patterns and guards

**Files:**
- Create: `src\Janet.Core\StorageCopySpec.cs`
- Create: `src\Janet.Core\StoragePattern.cs`
- Test: `tests\Janet.Tests\StorageCopySpecTests.cs`

**Interfaces:**
- Consumes: `GraphException` (existing).
- Produces:
  ```csharp
  public sealed record StorageCopyEnd(string Account, string Client);
  public sealed record StorageCopyPair(string Name, StorageCopyEnd Source, StorageCopyEnd Target);
  public sealed record BlobRule(StoragePattern Match, string? DropIfMetadata, StoragePattern? KeepIfLive, bool Copy);
  public sealed record ContainerSpec(StoragePattern Name, StoragePattern? Prefix, IReadOnlyList<BlobRule> Rules);
  public sealed record TableKeepIfLive(StoragePattern Container, StoragePattern Blob);
  public sealed record TableSpec(string Name, StoragePattern PartitionKey, StoragePattern? RowKey, TableKeepIfLive? KeepIfLive);

  public sealed class StorageCopySpec
  {
      public IReadOnlyList<StorageCopyPair> Pairs { get; }
      public IReadOnlyList<ContainerSpec> Containers { get; }
      public IReadOnlyList<TableSpec> Tables { get; }
      public static StorageCopySpec Load(string path);                 // absolute path required
      public static StorageCopySpec Parse(string json, string source); // source names the file in messages
      public StorageCopyPair Guard(string pairName);                   // spec section 5, guards 1-4
  }

  public sealed class StoragePattern
  {
      public string Text { get; }                                       // as written; doubles as the rule's name
      public IReadOnlyList<string> Placeholders { get; }                // in order, "client" included if used
      public static StoragePattern Parse(string text);
      public IReadOnlyDictionary<string, string>? Match(string value, string client); // null = no match
      public string Expand(IReadOnlyDictionary<string, string> captures, string client);
  }
  ```

- [ ] **Step 1: Write the failing tests** in `StorageCopySpecTests.cs`. Use a constant `Neelam`
  holding the spec section 4 JSON verbatim with accounts `"src"`/`"tgt"` (clients unchanged).

  ```csharp
  [Fact] PatternCapturesOneSegmentPerPlaceholder:
      Match("drafts/0a1b/20261031T101500Z.json", "c") on "drafts/{id}/{stamp}.json"
      == {id:"0a1b", stamp:"20261031T101500Z"}; Match("drafts/a/b/c.json", "c") is null.
  [Fact] ClientPlaceholderMatchesOnlyTheGivenClient:
      "settings/{client}/{stamp}.json" matches "settings/test-salon-one/x.json" for client
      "test-salon-one" and returns null for client "staging-salon".
  [Fact] ExpandWritesTheTargetClient:
      Expand({stamp:"x"}, "staging-salon") == "settings/staging-salon/x.json".
  [Fact] RowKeyPatternSplitsOnTheLiteralBetweenPlaceholders:
      "{id}_{stamp}" on "0a1b_20261031T101500Z" == {id:"0a1b", stamp:"20261031T101500Z"}.
  [Fact] TheNeelamSpecParses: Pairs.Count == 1, Containers.Count == 2, Tables.Count == 2,
      Containers[0].Rules[3].KeepIfLive!.Text == "drafts/{id}/{stamp}.json".
  [Theory] ARefusedSpecThrowsNamingTheProblem (each: Assert.Throws<GraphException>, message contains):
      unknown key "copyy" on a rule -> "copyy";
      keepIfLive "drafts/{other}/{stamp}.json" on "proofread/{id}/{stamp}.json" -> "other";
      keepIfLive "nowhere/{id}.json" (no rule matches it) -> "nowhere/{id}.json";
      two pairs named "p" -> "p";
      table keepIfLive using {id} with no rowKey -> "id".
  [Fact] LoadRefusesARelativePath: Load("storage-copy.json") throws, message contains "absolute".
  [Fact] GuardReturnsTheNamedPair: Guard("priya-to-staging").Target.Account == "tgt".
  [Theory] AFailedGuardThrows (message contains the quoted fragment):
      Guard("nope") -> "nope" (guard 1);
      a second pair whose target is "src" -> "src" (guard 3: source and target);
      a pair with source == target account and client -> "same" (guard 4).
  ```
  Guard 2 ("the target is listed as a target") holds by construction once guard 1 has found the
  pair; implement it as an assertion inside `Guard` and do not test it separately.

- [ ] **Step 2: Run, expect FAIL (types missing)**
  `janet check --target tests\Janet.Tests --test-filter StorageCopySpecTests` -> build errors
  naming `StorageCopySpec` / `StoragePattern`.

- [ ] **Step 3: Implement `StoragePattern` and `StorageCopySpec`.** A placeholder `{name}`
  compiles to a lazy single-segment group `(?<name>[^/]+?)`, the literal text between them
  `Regex.Escape`d, anchored `^...$`; `{client}` compiles to `Regex.Escape(client)` at match time.
  `Parse` walks the JSON by hand with an allowed-key set per object kind (root: pairs,
  containers, tables; pair: name, source, target; end: account, client; container: name, prefix,
  rules; rule: match, dropIfMetadata, keepIfLive, copy; table: name, partitionKey, rowKey,
  keepIfLive; table keepIfLive: container, blob) and refuses anything else with the JSON path.
  `copy` defaults to true. Messages name `source` and the path, e.g.
  `"<source>: containers[0].rules[3].keepIfLive uses {other}, which its match does not capture."`

- [ ] **Step 4: Run, expect PASS** -- same command; all StorageCopySpecTests green.

- [ ] **Step 5: Mutation-check** -- e.g. make the placeholder group `.+?` (crosses `/`); expect
  `PatternCapturesOneSegmentPerPlaceholder` to fail; restore.

- [ ] **Step 6: Commit**
  ```
  git add src/Janet.Core/StorageCopySpec.cs src/Janet.Core/StoragePattern.cs tests/Janet.Tests/StorageCopySpecTests.cs
  git commit -F <msg-file>   # subject: "A storage copy spec parses strictly and refuses a pair that could write its source"
  ```

---

### Task 2: Storage data-plane client, and StubAzure that records headers

**Files:**
- Create: `src\Janet.Core\StorageDataPlane.cs`
- Modify: `tests\Janet.Tests\StubAzure.cs:40,65` (Recorded gains headers)
- Test: `tests\Janet.Tests\StorageDataPlaneTests.cs`

**Interfaces:**
- Consumes: `AzureHttp.Send(HttpMethod, string url, string scope, JsonNode? body, IReadOnlyDictionary<string,string>? headers)`, `AzureHttp.Token(string scope)`, `AzureResponse`, `AzureErrors.Describe(AzureError)`, `StorageProbe.BlobApi`/`TableApi`.
- Produces:
  ```csharp
  public sealed record BlobItem(string Name, string ETag, long Length, string? ContentType, IReadOnlyDictionary<string, string> Metadata);
  public sealed record EntityItem(string PartitionKey, string RowKey, string ETag, JsonObject Body);

  public sealed class StorageDataPlane(AzureHttp http)
  {
      public IReadOnlyList<BlobItem>? ListBlobs(string blobEndpoint, string container, string? prefix);     // null = container 404
      public IReadOnlyList<EntityItem>? QueryPartition(string tableEndpoint, string table, string partitionKey); // null = table 404
      public AzureResponse PutBlobFromUrl(string blobEndpoint, string container, string name, string sourceUrl,
                                          string? contentType, IReadOnlyDictionary<string, string> metadata);
      public AzureResponse DeleteBlob(string blobEndpoint, string container, string name);
      public AzureResponse UpsertEntity(string tableEndpoint, string table, JsonObject entity);
      public AzureResponse DeleteEntity(string tableEndpoint, string table, string partitionKey, string rowKey);
      public static string BlobUrl(string blobEndpoint, string container, string name);
      public static JsonObject ForTarget(JsonObject entity, string targetPartitionKey);
  }
  ```
  StubAzure: `public sealed record Recorded(HttpMethod Method, string Url, string? Body, string? Authorization, IReadOnlyDictionary<string, string> Headers);`
  (request headers plus content headers, case-insensitive keys).

- [ ] **Step 1: Write the failing tests** in `StorageDataPlaneTests.cs` (StubAzure only):
  ```csharp
  [Fact] ListBlobsReadsEveryPageWithMetadata: two XML pages, the first with <NextMarker>m1</NextMarker>;
      result has both pages' blobs; second GET URL contains "marker=m1"; every GET contains
      "include=metadata"; a blob's Metadata["undone"] is read from <Metadata><undone>...</undone>.
  [Fact] ListBlobsReturnsNullForAMissingContainer: 404 with x-ms-error-code ContainerNotFound -> null.
  [Fact] ListBlobsThrowsWhenRefused: 403 AuthorizationPermissionMismatch -> GraphException containing that code.
  [Fact] QueryPartitionFollowsContinuation: first answer carries x-ms-continuation-NextPartitionKey: p2 and
      x-ms-continuation-NextRowKey: r2; second GET URL contains "NextPartitionKey=p2" and "NextRowKey=r2";
      Accept header is "application/json;odata=minimalmetadata"; filter is PartitionKey eq 'test-salon-one'.
  [Fact] PutBlobFromUrlSendsTheCopyHeaders: recorded PUT has x-ms-blob-type BlockBlob,
      x-ms-copy-source == source URL, x-ms-copy-source-authorization == "Bearer " + token,
      x-ms-blob-content-type image/png, x-ms-meta-title and x-ms-meta-undone absent/present as given,
      x-ms-version 2023-11-03, and no body.
  [Fact] BlobUrlEscapesEachSegment: BlobUrl("https://a.blob.core.windows.net/", "c", "images/Spring Offer #1.png")
      == "https://a.blob.core.windows.net/c/images/Spring%20Offer%20%231.png".
  [Fact] EntityUrlEscapesQuotes: DeleteEntity(..., "p", "a'b") sends DELETE to a URL containing
      "(PartitionKey='p',RowKey='a''b')" percent-encoded, with If-Match: *.
  [Fact] ForTargetRewritesThePartitionAndStripsServiceFields: input has odata.etag, odata.metadata,
      Timestamp, Timestamp@odata.type, ApprovedAt with ApprovedAt@odata.type Edm.DateTime;
      output PartitionKey == "staging-salon", RowKey unchanged, ApprovedAt and its annotation kept,
      the four service fields gone.
  [Fact] UpsertEntityPutsWithoutIfMatch: PUT to (PartitionKey=..,RowKey=..), no If-Match header, body is the entity.
  ```

- [ ] **Step 2: Run, expect FAIL** -- `janet check --target tests\Janet.Tests --test-filter StorageDataPlaneTests`.

- [ ] **Step 3: Implement.** StubAzure: record `request.Headers` and `request.Content?.Headers`
  into the new field (keep everything else). `StorageDataPlane`: list URL
  `{endpoint}/{container}?restype=container&comp=list&include=metadata[&prefix=..][&marker=..]`;
  query URL `{endpoint}/{table}()?$filter=PartitionKey eq '<pk>'[&NextPartitionKey=..&NextRowKey=..]`
  with `'` doubled and the filter `Uri.EscapeDataString`d; entity ETag from `odata.etag`. Segment
  escaping: split the name on `/`, `Uri.EscapeDataString` each segment, rejoin with `/`. Writes
  return the `AzureResponse` as-is and let network exceptions propagate (Task 4 decides).

- [ ] **Step 4: Run, expect PASS**, then run the whole suite once
  (`janet check --target tests\Janet.Tests`) since StubAzure changed.

- [ ] **Step 5: Mutation-check** -- drop the `marker` continuation; expect
  `ListBlobsReadsEveryPageWithMetadata` to fail; restore.

- [ ] **Step 6: Commit** -- subject: "Janet can list, copy and delete blobs and entities over the
  data plane with an Entra token".

---

### Task 3: The plan -- inventory, rules, blockers and the hash

**Files:**
- Create: `src\Janet.Core\StorageCopy.cs`
- Modify: `src\Janet.Core\StorageProbe.cs:146-169` (account lookup becomes shared)
- Test: `tests\Janet.Tests\StorageCopyTests.cs`

**Interfaces:**
- Consumes: Task 1 (`StorageCopySpec.Load`, `Guard`, `StoragePattern.Match/Expand`), Task 2
  (`StorageDataPlane`), `StorageProbe.Settings(JsonObject)`, `AzureSubscription.Resolve(string?)`.
- Produces:
  ```csharp
  // StorageProbe: was private instance FindAccount(StorageProbeRequest, AzureSubscription)
  internal static JsonObject FindAccount(AzureHttp http, string account, string? resourceGroup, AzureSubscription subscription);

  public sealed class StorageCopyRequest
  {
      public required string Spec { get; init; }      // absolute path
      public required string Pair { get; init; }
      public required string Mode { get; init; }      // "refresh" | "clear"; anything else throws
      public string? Rule { get; init; }              // a rule's match text, or a table name
      public string? Subscription { get; init; }
      public string? ResourceGroup { get; init; }     // holds BOTH accounts; omit when they differ
      public string? PlanHash { get; init; }          // apply only
  }

  public sealed record RuleCount(string Rule, int Count);
  public sealed record RuleCopy(string Rule, int Count, long Bytes);
  public sealed record ReasonCount(string Reason, int Count);
  public sealed record StorageCopyNames(IReadOnlyList<string> Delete, IReadOnlyList<string> Copy);

  public sealed record StorageCopyPlanResult
  {
      public const int ContractVersion = 1;
      public int Contract => ContractVersion;
      public required string Mode { get; init; }  public required string Pair { get; init; }
      public required string Spec { get; init; }
      public required StorageCopyEnd Source { get; init; }  public required StorageCopyEnd Target { get; init; }
      public required int DeleteBlobs { get; init; }  public required int DeleteEntities { get; init; }
      public required IReadOnlyList<RuleCount> DeleteByRule { get; init; }
      public required int CopyBlobs { get; init; }  public required long CopyBytes { get; init; }
      public required int CopyEntities { get; init; }  public required IReadOnlyList<RuleCopy> CopyByRule { get; init; }
      public required int DroppedCount { get; init; }  public required IReadOnlyList<ReasonCount> DroppedByReason { get; init; }
      public required int UnmatchedCount { get; init; }  public required IReadOnlyList<string> UnmatchedNames { get; init; } // first 100
      public required StorageCopyNames? Names { get; init; }
      public required bool ReadyToApply { get; init; }  public required IReadOnlyList<string> Blockers { get; init; }
      public required string PlanHash { get; init; }
  }

  public sealed class StorageCopy(AzureHttp http)
  {
      public static StorageCopy Shared { get; }
      public StorageCopyPlanResult Plan(StorageCopyRequest request);
      // Task 4 adds Apply. Plan and Apply share one private Inventory(...) that returns the
      // result plus the ordered operation list, so apply replays exactly what was hashed.
  }
  ```

- [ ] **Step 1: Write the failing tests.** In `StorageCopyTests` (IDisposable; writes the
  `Neelam` spec from Task 1's tests to a temp file `janet-storage-copy-<guid>.json`, deleted in
  `Dispose`), add a small world builder over StubAzure: ARM list answering both accounts with
  `primaryEndpoints` `https://src.blob.core.windows.net/` etc., per-container XML listings and
  per-table entity pages on each host. Tests:
  ```csharp
  [Fact] UndoneSavesAndTheirProofreadsAreDropped: source drafts/A/s1 live, drafts/B/s2 undone,
      proofread/A/s1, proofread/B/s2, proofread/C/s3 (no draft); copy names (rule = "proofread/{id}/{stamp}.json")
      == ["proofread/A/s1.json"]; DroppedByReason == [(noLiveSave, 2), (undone, 1)].
  [Fact] TheFirstMatchingRuleWins: proofread/images/h.json counts under "proofread/images/{hash}.json",
      not "proofread/{id}/{stamp}.json".
  [Fact] ApprovalRowsFollowTheirSave: approvals rows A_s1 and B_s2 -> CopyByRule for "approvals" has Count 1.
  [Fact] AnUnmatchedSourceBlobBlocks: source "stray/x.json" -> UnmatchedCount 1, ReadyToApply false,
      Blockers has one entry containing "match no rule".
  [Fact] TheTargetWipeStaysInScope: target settings container holds settings/staging-salon/s.json and
      _standard-baseline/b.json; DeleteByRule counts only the first; the second appears in no list.
  [Fact] RefreshFromAnEmptySourceOnlyDeletes: (Review Focus 4) source empty, target has 3 blobs and
      2 entities -> CopyBlobs 0, DeleteBlobs 3, DeleteEntities 2, ReadyToApply true.
  [Theory] AMissingContainerOrTableIsABlocker: (Review Focus 5) target container 404 / target table 404 /
      source container 404 -> ReadyToApply false, a blocker naming it, no exception.
  [Fact] ClearReadsOnlyTheTarget: mode clear -> no request to any "src." host at all; Copy* are 0.
  [Fact] AFailedGuardSendsNothing: pair "nope" -> GraphException and stub.Requests is empty.
  [Fact] TheHashMovesWithAnyETag: two plans identical -> same PlanHash; change one source ETag -> different.
  [Fact] RuleListsThatRulesNames: Rule "images/{name}" -> Names.Copy lists the image blobs; Rule omitted -> Names null;
      Rule "nope" -> GraphException listing the rule names.
  [Fact] MetadataValuesNeverAppear: a source blob with metadata title "Spring campaign" -> the plan
      serialized with AzureJson (Task 5) does not contain "Spring campaign". (Write this test in Task 5.)
  ```

- [ ] **Step 2: Run, expect FAIL** -- `janet check --target tests\Janet.Tests --test-filter StorageCopyTests`.

- [ ] **Step 3: Implement.** Order inside `Plan`: load spec, `Guard` (before any request), resolve
  subscription, find both accounts (one ARM list, or two GETs when `ResourceGroup` is given), then
  read. Refresh reads source and target; clear reads target only. Per container: scope is
  `Prefix` expanded for the side's client, else the whole container. Source blobs: first matching
  rule; `Copy == false` excludes; `DropIfMetadata` present -> dropped with reason = that key;
  `KeepIfLive` -> keep only if the expanded parent exists at source AND is live under its own
  rule, else dropped `noLiveSave`; no rule -> unmatched. Target blobs: deleted when in scope and
  matched by a rule with `Copy == true`, whatever their metadata. Tables: every target-partition
  entity is deleted; source entities copied unless the table's `KeepIfLive` parent is not live
  (`noLiveSave`). Operation list order: delete blobs, delete entities, copy blobs, copy entities,
  each sorted ordinally. Hash:
  ```
  lines = ["mode\t<mode>", "pair\t<name>", "source\t<account>\t<client>", "target\t<account>\t<client>"]
        + one line per operation: "<kind>\t<container-or-table>\t<name-or-pk/rk>\t<etag>"
  PlanHash = lowercase hex SHA-256 of string.Join("\n", lines sorted ordinal), UTF-8.
  ```
  Unmatched blocker text: `"<n> source blob(s) in scope match no rule; see unmatched.names, then add a rule or \"copy\": false."`
  `DeleteByRule`, `CopyByRule` and `DroppedByReason` are sorted ordinally by rule or reason, and
  list only non-zero rows; `UnmatchedNames` and `Names` lists are sorted ordinally.

- [ ] **Step 4: Run, expect PASS**; then the full suite (StorageProbe changed).

- [ ] **Step 5: Mutation-check** -- skip the `KeepIfLive` liveness check (existence only); expect
  `UndoneSavesAndTheirProofreadsAreDropped` to fail; restore.

- [ ] **Step 6: Commit** -- subject: "storage copy plans a refresh from live data only, and hashes
  exactly what it would do".

---

### Task 4: Apply -- stale, blocked, wipe, copy, retry, stop

**Files:**
- Modify: `src\Janet.Core\StorageCopy.cs`
- Test: `tests\Janet.Tests\StorageCopyTests.cs`

**Interfaces:**
- Consumes: Task 3's private inventory and operation list; Task 2's write methods.
- Produces:
  ```csharp
  public sealed record StorageCopyFailure(string Item, string Operation, AzureError Error);
  public sealed record StorageCopyClearCall(string Tool, string Spec, string Pair, string Mode); // Tool "storage_copy_apply", Mode "clear"

  public sealed record StorageCopyApplyResult
  {
      public const int ContractVersion = 1;
      public int Contract => ContractVersion;
      public required string Outcome { get; init; }      // applied | cleared | stale | blocked | failed
      public required string PlanHash { get; init; }     // the hash recomputed now
      public required int DeletedBlobs { get; init; }  public required int DeletedEntities { get; init; }
      public required int CopiedBlobs { get; init; }  public required long CopiedBytes { get; init; }
      public required int CopiedEntities { get; init; }
      public required StorageCopyFailure? Failure { get; init; }
      public required string? CopiedAt { get; init; }    // UTC "o" when the copy phase began; null if it never did
      public required StorageCopyClearCall? Clear { get; init; } // null in clear mode
  }

  public StorageCopyApplyResult Apply(StorageCopyRequest request); // PlanHash null or empty -> GraphException
  ```

- [ ] **Step 1: Write the failing tests:**
  ```csharp
  [Fact] TheSourceIsNeverWritten: run a refresh then a clear on a populated world; every recorded
      request with Method != GET has a host starting "tgt.". (Mutation below points one PUT at "src.")
  [Fact] AStaleHashWritesNothing: plan, then change one source ETag in the stub, apply with the old hash
      -> Outcome "stale", zero non-GET requests, PlanHash != the old one.
  [Fact] ABlockedPlanWritesNothing: unmatched source blob -> Outcome "blocked", zero non-GET requests.
  [Fact] RefreshDeletesThenCopies: the first non-GET is a DELETE, no DELETE follows the first PUT;
      counts equal the plan's; Outcome "applied"; CopiedAt not null; Clear == ("storage_copy_apply", spec, pair, "clear").
  [Fact] CopiedBlobsCarryMetadataAndContentType: the PUT for drafts/A/s1.json carries x-ms-meta-title,
      x-ms-blob-content-type application/json and x-ms-copy-source naming the src host and container "test-salon-one",
      and its own URL names container "staging-salon".
  [Fact] CopiedEntitiesLandInTheTargetPartition: the upsert body's PartitionKey is "staging-salon".
  [Fact] ClearOnlyDeletes: mode clear -> Outcome "cleared", no PUT, Clear null, CopiedAt null.
  [Fact] AThrottledCopyIsRetried: first PUT 429 with Retry-After: 120, second 201 -> Outcome "applied",
      stub.Slept == [30s].
  [Fact] ThrottlingThreeTimesIsTheFailure: three 503s -> Outcome "failed", Failure.Operation "copyBlob",
      stub.Slept has 2 entries, later operations never sent.
  [Fact] TheFirstFailureStops: the second copy PUT answers 403 -> Outcome "failed", CopiedBlobs 1,
      Failure.Error.Code from x-ms-error-code, no further PUT.
  [Fact] ADeleteThatFindsNothingIsNotAFailure: DELETE answers 404 -> counted as deleted, Outcome "applied".
  [Fact] AnUnreachableTargetIsAFailureField: a PUT throws HttpRequestException -> Outcome "failed",
      Failure.Error.Code "unreachable", no exception escapes.
  ```

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement `Apply`.** Recompute the inventory; hash mismatch -> `stale`; blockers ->
  `blocked`; else run operations in list order. Retry lives only around copy operations: on 429
  or 503, `http.Sleep(clamp(Retry-After seconds, default 1, 1..30))`, at most 3 attempts in all.
  A DELETE answering 404 counts as done. Any other non-success, or `HttpRequestException` /
  `TaskCanceledException` (as `AzureError("unreachable", message, null, [])`), stops with
  `failed` and the counts so far. `Item` is `"<container>/<name>"` or `"<table>/<pk>/<rk>"`.

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Mutation-check `TheSourceIsNeverWritten`** -- send one copy PUT to the source
  endpoint; expect the test to fail; restore. Also drop the stale check; expect
  `AStaleHashWritesNothing` to fail; restore.

- [ ] **Step 6: Commit** -- subject: "storage copy applies only the plan it was shown, and never
  writes its source".

---

### Task 5: Envelopes, schemas, MCP tools, contract gate and catalog

**Files:**
- Modify: `src\Janet.Core\AzureJson.cs` (two `Serialize` overloads)
- Create: `contracts\storage-copy-plan.schema.json`, `contracts\storage-copy-apply.schema.json`
- Modify: `src\Janet.Mcp\AzureTools.cs` (two tools, after `StorageProbe`)
- Modify: `src\Janet.Mcp\Narrowing.cs:76-77` (two rows)
- Modify: `scripts\Test-OutputContracts.ps1:286-299` (two samplers after `storage-probe`)
- Modify: `tests\Janet.Tests\AzureSchemaTests.cs` (Schemas list, contract theory, two envelope facts)
- Modify: `tests\Janet.Tests\StorageCopyTests.cs` (`MetadataValuesNeverAppear`)
- Modify: `README.md:53` (the Azure row names both tools)
- Catalog: `research_update` on `file.azure-tools`

**Interfaces:**
- Consumes: Tasks 3-4 result records.
- Produces: `AzureJson.Serialize(StorageCopyPlanResult result, bool pretty = false)`,
  `AzureJson.Serialize(StorageCopyApplyResult result, bool pretty = false)`; MCP tools
  `storage_copy_plan(spec, pair, mode, rule?, subscription?, resourceGroup?)` and
  `storage_copy_apply(spec, pair, mode, planHash, subscription?, resourceGroup?)`.
  Plan JSON keys: `contract, mode, pair, spec, source{account,client}, target{account,client},
  delete{blobs,entities,byRule[{rule,count}]}, copy{blobs,bytes,entities,byRule[{rule,count,bytes}]},
  dropped{count,byReason[{reason,count}]}, unmatched{count,names[]}, names(null|{delete[],copy[]}),
  readyToApply, blockers[], planHash`. Apply JSON keys: `contract, outcome, planHash,
  deleted{blobs,entities}, copied{blobs,bytes,entities}, failure(null|{item,operation,error}),
  copiedAt, clear(null|{tool,spec,pair,mode})`, with `error` in the shared ARM shape
  (`code, message, target, details[]`) used by the other Azure schemas.

- [ ] **Step 1: Write the failing tests** in `AzureSchemaTests`: add both files to `Schemas` and
  to `TheSchemaStampsTheContractTheCodeEmits`; add
  `TheStorageCopyPlanEnvelopeEmitsExactlyWhatItsSchemaDeclares` (a real `Plan` over StubAzure
  with `Rule` set so `names` is an object, one unmatched blob, one drop of each reason; `Matches`
  on root, source, target, delete, delete.byRule[], copy, copy.byRule[], dropped,
  dropped.byReason[], unmatched, names) and
  `TheStorageCopyApplyEnvelopeEmitsExactlyWhatItsSchemaDeclares` (a real failed `Apply` so
  `failure`, `copiedAt` and `clear` are all present; `Matches` on root, deleted, copied, failure,
  failure.error, failure.error.details[] via `SharedShapes`-style checks, clear). Add
  `MetadataValuesNeverAppear` to `StorageCopyTests`.

- [ ] **Step 2: Run, expect FAIL** (schema files missing).

- [ ] **Step 3: Implement** the serializers (every key present, nulls explicit), the two schemas
  (copy `storage-probe.schema.json`'s `$janet` block; `produces` "storage_copy_plan (MCP only; no
  CLI verb)" and likewise for apply; `surface` lists `StorageCopy.cs`, `StorageCopySpec.cs`,
  `StoragePattern.cs`, `StorageDataPlane.cs`, `AzureJson.cs`, `AzureTools.cs`; node
  `file.azure-tools`; a `$comment.metadataValues` saying no field carries a metadata value), and
  the two tools. Descriptions:
  - plan: "READ-ONLY. What a refresh (or clear) of a staging copy would delete and copy, from a
    JSON spec of named source/target pairs and per-container rules. Reads both accounts over the
    data plane; refresh copies only live items (dropIfMetadata, keepIfLive), clear touches only
    the target. Returns counts by rule, dropped items by reason, unmatched source blobs (which
    block apply), blockers, and planHash, which storage_copy_apply requires. Blob names appear
    only with rule=<match text or table name>; metadata values never. A pair whose target is a
    source anywhere is refused before any request."
  - apply: "CHANGES LIVE AZURE. Performs exactly the plan storage_copy_plan showed: recomputes it,
    and writes nothing if planHash no longer matches (stale) or the plan is blocked. Refresh
    deletes the target client's in-scope data, then copies server-side (Put Blob From URL) and
    upserts entities under the target client; clear only deletes. Stops at the first failure with
    partial counts. Never writes the source account. Returns copiedAt and the exact clear call."
  Narrowing rows: `["storage_copy_plan"] = "drop rule if you set it; names are listed only for
  one rule at a time."`, `["storage_copy_apply"] = BoundedByConstruction`. Samplers:
  `storage-copy-plan` skips unless `JANET_SAMPLE_COPY_SPEC` and `JANET_SAMPLE_COPY_PAIR` are set
  (then samples mode refresh); `storage-copy-apply` always returns `skipped = 'a write tool is
  never sampled; shape held by AzureSchemaTests'`. README row: add `storage_copy_plan` /
  `storage_copy_apply` beside `storage_probe`. Catalog: `research_update` `file.azure-tools`
  appending to its summary the two tools and a caveat: "storage_copy_apply wipes the target
  client's in-scope data before copying, and the neelam accounts have no soft delete: run the
  plan first and do not refresh while anyone is testing on staging."

- [ ] **Step 4: Run, expect PASS:** `janet check --target tests\Janet.Tests` (whole suite,
  including `ToolSurfaceTests`' narrowing-row check), then
  `pwsh scripts\Test-PowerShellRules.ps1 -Path scripts\Test-OutputContracts.ps1` clean, then
  `pwsh scripts\Test-OutputContracts.ps1` reporting both new contracts as skipped, not
  `no-sampler`.

- [ ] **Step 5: Mutation-check** -- add a stray key to the plan serializer; expect the plan
  envelope test to fail; restore.

- [ ] **Step 6: Commit** -- subject: "storage_copy_plan and storage_copy_apply are on the Janet
  MCP server, each held to its schema".

---

### Task 6: neelam -- the spec file, its pin test and the corrected records

Repo `C:\repos\neelam-aesthetics`, its own commit. Graph `neelam`; rebuild it before relying on
answers after edits.

**Files:**
- Create: `infra\storage-copy.json` (spec section 4, real accounts)
- Modify: `tests\Neelam.Campaigns.Tests\Neelam.Campaigns.Tests.csproj` (copy the JSON to output:
  `<None Include="..\..\infra\storage-copy.json" Link="storage-copy.json" CopyToOutputDirectory="PreserveNewest" />`)
- Create: `tests\Neelam.Campaigns.Tests\StorageCopySpecTests.cs`
- Modify: `src\Neelam.Campaigns.Storage\CampaignStore.cs:331` and `ImageLibrary.cs:50`
  (`private const` prefixes become `internal const` so the test can read them; visible to
  `Neelam.Campaigns.Tests` already)
- Modify: `WIP.md:760`, `README.md` (operating rule), `infra\test.bicepparam:1`, `infra\main.bicep:10-11`

**Interfaces:**
- Consumes: `UndoMark.Key`, `TableMetadata.ApprovalsTable`, `KnownItemTable.Table`,
  `ClientStores.SettingsContainer`/`CatalogPrefix`/`PolicyPrefix`/`BaselinePrefix`/`LaddersPrefix`,
  `ProofreadJson.Prefix`, `CampaignStore.ProofreadPrefix`, `ImageLibrary.Prefix`, and the
  `DocumentKind` folder names at `CampaignStore.cs:518-519`.
- Produces: `infra\storage-copy.json`, read by Janet's `storage_copy_plan`.

- [ ] **Step 1: Write the failing test** `StorageCopySpecTests.EveryRulePrefixIsOneTheStoreWrites`:
  parse `storage-copy.json` with System.Text.Json; assert every `dropIfMetadata` equals
  `UndoMark.Key`; the table names are exactly `{KnownItemTable.Table, TableMetadata.ApprovalsTable}`;
  the second container is `ClientStores.SettingsContainer`; and each rule's leading literal
  segment is in the set built from the constants above (drafts, templates, proofread/,
  proofread/images/, catalog, policy, baseline, ladders, images/). Plus
  `HerAccountIsOnlyEverASource`: no pair's target account equals any pair's source account.
- [ ] **Step 2: Run, expect FAIL** (file missing) -- neelam's own test command, not bare `dotnet test`
  if neelam's CLAUDE.md names one; otherwise `janet check --target tests\Neelam.Campaigns.Tests`.
- [ ] **Step 3: Create the JSON and the csproj link; widen the two constants.**
- [ ] **Step 4: Run, expect PASS.** Mutation: rename one rule's `drafts` to `draft`; expect
  failure; restore.
- [ ] **Step 5: Correct the records.** WIP.md:760 becomes the owner-approved temporary exception
  (spec section 1 wording: a copy made with Priya's knowledge, live data only, refreshed before
  testing and cleared after, nothing copied back, she does not see staging). README gains the
  operating rule (do not refresh while anyone is testing on staging; clear when tests are done;
  the clear call is in the refresh envelope). The two Bicep comments stop saying "no client data".
- [ ] **Step 6: Commit** (neelam repo) -- subject: "Staging can hold a temporary copy of Priya's
  live data, and the copy spec is pinned to the store".

---

### Task 7: Live proof, one owner go-ahead per step

No code. Each step waits for the owner's explicit yes.

- [ ] **Step 1:** Rotate the Janet MCP server (`server_rotate`, authorized after review) and
  confirm `storage_copy_plan` and `storage_copy_apply` are served.
- [ ] **Step 2 (read-only):** `storage_copy_plan` spec=`C:\repos\neelam-aesthetics\infra\storage-copy.json`
  pair=`priya-to-staging` mode=`refresh`. Report counts, drops, blockers to the owner.
- [ ] **Step 3 (owner go-ahead):** `storage_copy_apply` with that `planHash` (permission prompt).
  Watch for HTTP 411 on the first PUT (Review Focus: Content-Length on a bodyless PUT).
- [ ] **Step 4:** Add a neelam thread item: "staging holds Priya's data since <copiedAt> -- clear
  when tests are done", with the returned `clear` call in its notes. Owner smoke-checks staging.
- [ ] **Step 5 (when tests are done, owner go-ahead):** plan mode `clear`, apply with its hash,
  complete the thread item.
