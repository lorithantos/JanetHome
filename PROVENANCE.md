# Provenance

Record of what was carried over from the 2026-07-01 departure handoff corpus into this
repo, where each item came from, and the basis for judging it portable.

**Extraction date:** 2026-07-27
**Source:** `E:\Janet\handoff\` (removable drive) — 640 files, built 2026-05-14 to 2026-07-01
**Extracted:** 27 files
**Left behind:** ~613 files

---

## Standard applied

A file was carried over only if it met **all** of:

1. No employer identifiers — org URLs, project/repo GUIDs, area paths, internal
   product or service names, cluster or database names.
2. No colleague names, aliases, or personal data.
3. No production telemetry, incident data, or customer-facing URLs.
4. No security-posture information.
5. Generic utility or original analysis — not coupled to employer systems, and not
   derived from proprietary internals.

**Derivation is the test; resemblance is not evidence of it.** *(Clarified 2026-08-08.)*
Point 5 turns on where a thing came from, not what it looks like when finished.
Rebuilding a general technique from its principles is legitimate even when you first
built one at an employer and the result resembles it — an SWR cache, an LSP-backed
tool, a coalescing queue, a code graph over a compiler's semantic model are all
techniques belonging to the field, and knowing one works is not proprietary knowledge.
`RazorGraph.Mcp` is exactly this and is the strongest thing in the collection: the
original was employer work product and stayed behind; the pattern was rebuilt from
scratch on public codebases and a public SDK (DESIGN-NOTES §4).

What the standard actually excludes is employer-*specific* content — their source,
the topology of their systems, their data, their people. "Architecture" was used
loosely in earlier wording and read far wider than intended; it means *how their
systems are wired*, never architecture as a discipline. A rule broad enough to forbid
reimplementing a known-good pattern would forbid most of this repo, and would be
wrong on the merits rather than merely inconvenient.

Verified mechanically after extraction: a 31-term identifier sweep over all 27 files
returns zero hits. All 22 scripts parse cleanly. All files are UTF-8 without BOM.

---

## Carried over

### `scripts\` — 22 PowerShell utilities

**Verbatim, zero employer references (9):**

| File | What it does |
|---|---|
| `Fix-FileEncoding.ps1` | Two-pass non-ASCII→ASCII cleanup; handles raw Windows-1252 bytes in the 0x80–0x9F range that .NET's UTF-8 decoder silently destroys |
| `Test-FileEncoding.ps1` | Encoding audit companion to the above |
| `ConvertTo-MermaidEmbed.ps1` | Mermaid source → inline `<img>` via mermaid.ink; can process whole markdown files |
| `Read-JsonCache.ps1` | Cached JSON reads |
| `Get-UsingMap.ps1` | Maps `<Using Include=...>` directives across csprojs |
| `Get-NamespaceMap.ps1` | Namespace inventory for a C# tree |
| `Get-LspReferences.ps1` | Language-server reference lookup |
| `Invoke-NamespaceMigration.ps1` | Bulk namespace rename |
| `New-RepoStatusPage.ps1` | Repo status HTML generation |

**Verbatim; only reference is `$env:JanetBase`, your own framework variable (8):**

`New-TextFile.ps1` (here-string/BOM escape hatch — base64 input mode avoids all
PowerShell quoting pain), `ConvertTo-Base64File.ps1`, `Read-TextFile.ps1`,
`Invoke-SurgicalEdit.ps1` (JSON-plan-driven deterministic edits — see DESIGN-NOTES §5),
`Get-ScriptCatalog.ps1`, `Pop-ThreadStack.ps1`, `Show-ThreadStack.ps1`,
`Test-PreCommit.ps1`.

**Sanitized before copying (5):**

| File | Change made |
|---|---|
| `Push-ThreadStack.ps1` | `.EXAMPLE` line referenced an internal cache investigation and a prod database — replaced with a generic example |
| `Open-InVisualStudio.ps1` | Two doc-comment examples named an internal solution — replaced with `MyApp.Services` |
| `Get-ProjectSurvey.ps1` | Three `.EXAMPLE` lines named real repos — replaced with `SampleApp.*` |
| `Get-ConfigInventory.ps1` | Same, three lines |
| `Get-TypeOverlap.ps1` | Parameter `$OtherPath`, internal variables, and output JSON keys carried an internal product name — renamed throughout; added a synopsis block. Logic unchanged: reports types sharing a short name across two C# trees |

In every case the change was to documentation examples or identifier names. No
algorithm or logic was altered, and nothing proprietary was reproduced.

### `notes\` — 5 documents

| File | Origin | Basis |
|---|---|---|
| `insertion-mergesort-hybrid.md` | Personal research note, 2026-04-24 | Pure algorithms. Tagged "shower thought / not-actually-needed" by its author. Zero employer content |
| `insertion-mergesort-hybrid-full.md` | Same, expanded (56 KB) | Same |
| `dotnet-object-sizing.md` | Personal research note | Concerns a public dotnet/runtime issue (#24200) and public .NET behavior. Zero employer content |
| `meta-ai-second-brain-source.md` | Notes on a **public** Meta engineering blog post (~2026-06) | Third-party public material. Zero employer content |
| `meta-second-brain-vs-janet.md` | Original comparative analysis, 2026-06-24 | Your own architectural argument. **Sanitized** — see below |

`meta-second-brain-vs-janet.md` sanitization: removed the named colleague it was
addressed to; generalized the internal MCP roster to functional categories (issue
tracker, telemetry engine, incident management, chat, mail, document store);
generalized four references to the internal incident-management system. A sanitization
notice was added at the top of the file. The architectural argument is unchanged.

### Root

| File | Notes |
|---|---|
| `DESIGN-NOTES.md` | **Newly written**, not copied. Captures the transferable patterns in prose — manifest-driven startup, progressive disclosure, thread stack, graph-first analysis, deterministic edits, query routing, circuit-breakers, per-scope storage, the handoff-corpus format. Written from memory and reasoning rather than derived from employer artifacts. On 2026-08-08 the sections you consult once rather than operate by (6, 10, 11, 12) moved to `notes\build-retrospective.md` and `notes\discriminator-front-end.md`; same origin and same standard, retrieved instead of read on every start |
| `PROVENANCE.md` | This file |
| `README.md` | Orientation |

---

## Deliberately left behind

| Category | Approx. files | Reason |
|---|---|---|
| Full git-branch snapshot of a private employer repo | 260 | Employer source code. Copied off the work machine on the last day, including 26 C# files and a complete MCP server implementation |
| Frozen snapshots of two more private repos | 180 | Same |
| Incident investigation artifacts | 30 | Production telemetry — request logs, pod names, customer-facing URLs, cache outcomes, plus a 20 KB incident diagnosis |
| Security-assessment tooling | ~40 | KeyVault→RBAC migration, service-principal hygiene scans, credential audits, vulnerability dashboards. No secrets in it, but it maps where the soft spots are |
| Framework dotfiles snapshot | 29 | Org URL, project GUID, 11 repo IDs, area path, 9 colleague aliases, cluster shapes, runbooks |
| Architecture / landmines / decisions / runbooks / in-flight work | 21 | Internal system knowledge |
| Colleague behavioral profile (2 copies) | 2 | Personal data about a named third party, built from 403 of their review comments across 214 PRs. Deleted first, ahead of the source code |
| Executive briefing deck + plans + backups | 6 | Internal deliverable |
| In-flight work notes, KT meta-artifacts, task inventory | 12 | Internal |
| Research archive (`.zip`) | 62 entries | ~55 internal; the 3 portable entries were extracted individually and are listed above |
| ADO/telemetry-coupled automation scripts | ~64 | **Judgment call.** PR triage, sprint board, morning briefing, work-item linking. The workflow design is original work, but the scripts were authored on employer time against employer systems, which in most employment agreements makes them employer property regardless of authorship. The *patterns* are captured in `DESIGN-NOTES.md`; the code was not carried over |

---

## Notes for the record

- **No credentials were found anywhere in the source corpus.** A sweep for tokens,
  connection strings, KeyVault contents, private keys, and PATs across all 640 files
  returned no live secrets. Every script acquires tokens at runtime via `az` or
  SecureString. This was a confidential-material question, not a credential-leak one.
- The source corpus remains on `E:\` and has not been modified or deleted by this
  extraction. Disposition of the removable drive is a separate decision.
- Nothing in this repo reproduces employer source code, internal architecture,
  production data, or personal information about colleagues.
