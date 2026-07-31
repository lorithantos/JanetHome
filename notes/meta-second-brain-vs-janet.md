🌵 # Meta's "AI Second Brain" vs. Janet

**Source:** Analytics at Meta — *AI Second Brain: How we built it and what we learned* (~2026-06 Medium post)
**Author of this comparison:** Janet (Lori's framework), 2026-06-24
**Audience:** originally written for a colleague adopting the framework.
**Sanitized:** 2026-07-27 for personal retention -- employer tool names and colleague references generalized.

## TL;DR

Meta published an internal "AI Second Brain" toolkit that, structurally, is the same shape as Janet: filesystem + markdown skills + MCP infrastructure + progressive disclosure + harness-agnostic agent (theirs runs on Claude Code, ours on Copilot CLI). Convergent evolution, not derivative — they hadn't published when we converged on this.

Three things worth lifting from their design. Three things they don't have that we should keep doing.

## What they built

| Layer | What it is |
|---|---|
| **PARA workspace** | Projects / Areas / Resources / Archives folder structure, each with its own `CLAUDE.md` |
| **Root `CLAUDE.md`** | Identity + active-portfolio summary loaded at session start |
| **Infrastructure layer** | MCPs + internal CLIs for doc editors, messaging, task trackers, code review |
| **Agent harness** | Claude Code (they note: harness-agnostic) |
| **Skills as markdown** | Reusable workflows anyone can write; no compile/deploy |
| **Bootstrap skill** (`/para-init`) | Scans your recent activity, *proposes* a workspace, populates project contexts |
| **Domain skills** | `/start-project`, `/read-meeting-notes` (routes notes to projects via weighted scoring), `/debrief:team` (rolls up a team's work in parallel) |
| **Team layer** | "Third Brain" in pilot — shared knowledge across teammates |

Adoption: 60K installs / 10K DAU at Meta in ~3 months, snowballed after one non-technical PM posted an install guide.

## Side-by-side

| Dimension | Meta Second Brain | Janet |
|---|---|---|
| Personal portfolio organization | **PARA**, lifecycle-tagged | `wip.json` + `daily-log.md` + ad-hoc `research/`. **No lifecycle layer.** |
| Per-project context | Per-project `CLAUDE.md` | Per-skill `SKILL.md` + per-repo `.github/instructions/` + repo memories |
| Root / session-start | `CLAUDE.md` | `copilot-instructions.md` + `janet.md` + **`startup-manifest.json`** (explicit mechanical read list) |
| Progressive disclosure | Yes, free-form | Yes, manifest-driven |
| Infrastructure layer | Meta MCPs + internal CLIs | Work MCPs (issue tracker, telemetry/query engine, incident management, chat, mail, document store) + `.github/scripts/` |
| Storage backend | Shared cloud storage; tripped API rate limits, required 10x capacity increase | Azure Blob per-scope; effectively free at team scale; no shared ceiling |
| Bootstrap from existing activity | `/para-init` infers projects from posts/docs/tasks/CRs | `Install-JanetFramework.ps1 -InitTeam` **scaffolds**; doesn't infer |
| Cross-project routing | `/read-meeting-notes` routes by weighted scoring | **None** — routing is by skill/playbook, not by project |
| Team / shared layer | "Third Brain" in pilot | `team.json` + reviewer personas + shared scripts repo. **More config than knowledge graph.** |
| Operational discipline | Not emphasized | Heavy — query two-question routing, incident-tool circuit-breaker, verification-before-summary, MCP profile awareness, etc. |
| Persona | Generic | Named (Janet); Eleanor Protocol |

## Three things worth lifting

1. **PARA-style lifecycle layer in the personal-context tier.** PARA gives the agent a durable map of *what's active right now*. Janet has `wip.json` (a thin slice of "active Projects") and a daily-log, but no explicit Projects/Areas/Resources/Archives distinction. A first prototype would be `$env:JanetBase\.github\portfolio\` with `projects/`, `areas/`, `archives/` subdirs, each holding short markdown files the startup manifest loads. Cheap to try; would tell us fast whether the lifecycle vocabulary helps or just adds bureaucracy. Use our team's actual operational concepts (sprint / DRI rotation / cert rotation / PR backlog) rather than PARA's vocabulary verbatim.

2. **Activity-inferring bootstrap.** Their `/para-init` reads your recent posts/docs/tasks and *proposes* a workspace. Our installer scaffolds; it doesn't infer. For a teammate adopting Janet, a "scan my issue tracker + recent PRs + recent incidents + recent chat threads and propose initial `team.json` + `wip.json` + portfolio" skill would dramatically lower the entry cost. Today new adopters get a working toolkit but have to populate their own context.

3. **Cross-project routing of incoming artifacts.** No `/read-meeting-notes` equivalent. If a stand-up note or a Teams thread came in, Janet has no mechanism to route it into the right work-item or project context. Our `daily-log.md` is hand-curated. A skill that ingests meeting transcripts, weighted-scores against active work items, and appends to the right portfolio file would close this gap.

## Three things they don't have that we should keep doing

1. **Operational discipline.** Their framing is "knowledge work assistant." Janet is a production-engineer-on-call assistant. telemetry query discipline, incident ack workflows, PR review tiers, sprint ops, certificate rotation, DRI handoff hygiene — none of that shows up in their post. They mention tripping shared-cloud-storage API rate limits as a growth pain; the framework carries a circuit-breaker section because the incident-management MCP times out, and we catch it inside the agent before it cascades. The discipline is what makes Janet trustworthy for live-site work, and trustworthy-for-live-site is the differentiator.

2. **Manifest-driven startup.** Their root `CLAUDE.md` is a summary file the agent reads. Our `startup-manifest.json` is an explicit "read these files, run these commands, in this order, mechanically" contract. It's harder to drift, harder to silently break, and easier to validate. We should keep it and probably document it as a pattern.

3. **Per-scope storage that doesn't have a shared ceiling.** Their post mentions tripping shared-cloud-storage API rate limits hard enough to require a 10x capacity increase — centralized convenience, centralized failure mode. Janet on Azure Blob is per-user / per-team container, SAS-scoped, effectively free at any plausible team scale. We don't have to engineer around a shared rate-limit because there isn't one to share. This is architectural, not operational discipline — a different point than the incident-tool circuit-breaker, and it stacks.

## What I would not copy

- **PARA vocabulary as-is.** "Areas/Resources/Archives" maps awkwardly to our work. Use our team's operational concepts.
- **Their viral-evangelism adoption model.** Worked because Meta has 60K knowledge workers with meeting notes. Our team is smaller and the value-prop is operational rigor, not productivity. Adoption probably looks more like "two teammates install, see where friction is, then write the guide."
- **Optimizing for personal-productivity skills before operational ones.** Their first hit was meeting-note processing. The first hits should remain telemetry / PR / incident / sprint — that's where the actual time goes.

## One concrete question for you

If we built a PARA-style portfolio layer, what's the right vocabulary? My instinct is:

- `projects/` → active work items, design docs, in-flight PRs (1-6 weeks)
- `rotations/` → DRI rotation, cert rotation, sprint hygiene (recurring responsibilities)
- `reference/` → architecture analyses, repo guides, cluster shape docs
- `archive/` → completed projects + closed rotations

But you've adopted Janet recently enough to have an outside view. What would you have wanted on day one?

— Lori (drafted by Janet)
