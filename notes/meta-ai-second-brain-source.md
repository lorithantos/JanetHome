# AI Second Brain: How we built it and what we learned

**Author:** Analytics at Meta
**Published:** ~2026-06 (Medium)
**Captured by:** Lori Woods, 2026-06-24
**Companion doc:** [`meta-second-brain-vs-janet.md`](./meta-second-brain-vs-janet.md)

---

Knowledge workers at Meta routinely contend with workflow fragmentation, where critical information — including meeting notes, tasks, key decisions, and code context — is siloed across disparate platforms. Each new AI conversation starts cold: the same explanations, the same links, the same ten minutes of context-setting before any real work begins.

So we tested a simple hypothesis: what if an AI agent had persistent, structured access to everything a person is working on, and carried that context across every interaction? Not a chatbot that answers questions, but a working partner that tracks projects, reads meeting notes, surfaces connections, and builds on prior conversations.

That AI Second Brain experiment, born in the analytics org, has since been adopted by over 60,000 people across Meta: engineers, PMs, designers, legal, finance, communications, and sales. This post covers how it was built, how it grew, and what we learned.

## How It Works

The AI Second Brain has four main pieces.

### The PARA Workspace: A Folder Structure That Agents Understand

The idea started with an existing productivity framework. Tiago Forte's PARA method organizes all personal information into four categories: Projects (short-term, active work), Areas (long-term responsibilities), Resources (reference material that may be useful in the future), and Archives (inactive items from the other three categories, stored for future reference). It was designed for human note-taking, but it turned out to be equally useful for AI agents. It tells the agent not just what information exists, but what's active, what's important, and where new information should go.

A PARA workspace gives the agent a durable map of a person's work. When a user opens a new session, the agent already knows their active projects, team structure, working conventions, and recent activity. When a meeting note arrives, the agent reads the content, matches it against known project keywords, and files it into the right folder without being told where it goes.

This structure also solves a practical problem: context windows are finite. Loading every document for every project into every session wastes tokens and degrades output quality. Instead, the agent starts each session with a lean root context `CLAUDE.md` (a summary of who you are and what you're working on) and drills into specific project folders only when the conversation requires it, loading each project's `CLAUDE.md` file into context only when opening those folders. This concept is known as **progressive disclosure**, and it turned out to be one of the most important design decisions. Lean context up front, deeper detail on demand.

Most coding agents like Claude Code recommend partitioning work into projects, each with its own `CLAUDE.md`. That pattern works when "project" maps cleanly to a coding repo you `cd` into; knowledge work is messier. Meeting notes, docs, decisions, and tasks span many projects at once, projects come and go, and most aren't repos at all. PARA's per-project `CLAUDE.md` files line up with this default, but everything above them is new: a root `CLAUDE.md` that holds your identity and active portfolio across every session, plus a lifecycle layer (Projects, Areas, Resources, Archives) that tells the agent which projects matter right now and where new information should go. That root layer is what makes cross-project skills possible, like reading meeting notes and routing them to the right projects, or generating team reports that pull from everyone's work.

### The Infrastructure Layer: The Bridge to Internal Tools

Most of the information that knowledge workers need lives not in local files but in the tools they use: document editors, messaging platforms, task trackers, code review systems, wikis. The investment that made this project possible was Meta's development of MCPs (Model Context Protocol servers) and CLIs (Command Line Interfaces) that give AI agents authenticated, scoped access to these systems.

Without this layer, the agent can only read local files. With it, the agent can pull meeting transcripts, check task status, read discussion threads, and write documents, all within the user's own permissions.

### The Agent: The Execution Engine

An AI agent is more than a model. It is a model plus a harness: the execution environment, tools, and orchestration logic that turn a question-answering system into a work engine. Without a harness, a model can respond to prompts. With one, it can run bash commands, read and write files, call APIs, recover from errors, and chain decisions in a loop until the task is done.

A capable harness provides the agentic loop (reason, act, observe, repeat), filesystem access, tool calling, MCP integration, and error recovery that sustained knowledge work requires. It does not just answer questions about your projects; it navigates folders, runs searches, calls CLIs, and writes files on your behalf. (At time of writing, our deployment runs on Claude Code with the latest Anthropic model; the architecture is harness-agnostic.)

### The Skills: Workflows as Markdown

Skills are reusable instructions encoded as plain markdown files plus some scripts: no compiled code, no servers, no deployment pipeline. Each skill tells the agent how to complete a specific workflow, step by step. Because they are just text, anyone (even the agents themselves) can write, modify, and share them. This turned out to be one of the strongest drivers of both adoption and community contribution. Some examples:

- **`/para-init`** bootstraps a new workspace from scratch. The agent scans the user's recent posts, documents, tasks, wikis, and code reviews to infer what projects they are working on. It proposes a folder structure, generates context files for each discovered project, and populates them with relevant resources. A user goes from nothing to a fully structured workspace in a single session, with no manual file organization required.
- **`/start-project`** creates a new project from a brain dump. The user describes what they are working on in free text: goals, stakeholders, open questions, relevant links. The agent then runs deep research across internal tools in parallel, looking for related documents, discussions, and prior work. It presents what it found, proposes a project structure, and creates everything (folder, context file, brief, initial tasks) after the user confirms.
- **`/read-meeting-notes`** processes AI-generated meeting transcripts incrementally. The agent scans configured sources (document folders, local files), identifies notes it has not yet processed, extracts action items and decisions, and routes each note to the most relevant project using a weighted scoring system based on keyword matches, stakeholder overlap, and explicit project mentions. Running this daily keeps project backlogs current without manual effort.
- **`/debrief:team`** generates a manager-level view of what an entire team accomplished over a given period. The agent resolves the org tree, then launches individual work digests for every team member in parallel, each sourcing artifacts from code reviews, tasks, posts, and documents. It synthesizes the results bottom-up into a portfolio-style report organized by project, not by person, and outputs a shareable HTML page. For a team of 10, this runs in minutes and replaces hours of status-gathering.

These are just a few of the skills that come preloaded with the Second Brain. The community has since created thousands more, available in an internal library for any employee to install. Users also routinely write their own for repetitive workflows specific to their role or team, often in under an hour.

## The Snowball: 0 to 63,000 in Three Months

Adoption was a slow burn until early February, when a non-technical PM published a post titled "I finally built my second brain. Here's why you should too." He paired a non-engineer install guide with concrete examples of what it unlocked: drafting documents from live meeting transcripts, synthesizing leadership summaries in minutes, tracking projects across weeks. Within days the system had spread across every function at Meta.

Soon after, growth outpaced infrastructure: the plugin's shared cloud storage integration tripped API rate limits and slowed Meta's broader AI dev environments, requiring a 10x capacity increase. The incident was the cleanest signal that adoption was real, organic, and accelerating well past what we'd planned for.

Today the plugin has over 63,000 installs across every organizational pillar at Meta, with roughly 10,000 daily active users. The community has built 9 discipline-specific packages (for PMs, data scientists, engineers, designers, and more), a team-level shared context system, and integrations for automated meeting processing, career development tracking, and visual reporting.

## What We Learned

**Infrastructure comes first.** The agent is only as useful as the systems it can reach. Authenticated access to internal tools (document editors, messaging platforms, task trackers, code review) is what separates a useful agent from a chatbot. Organizations building AI agent workflows should invest in this infrastructure layer before anything else (highly recommend this piece on how to build useful CLIs). The applications will follow, often from unexpected directions.

**Progressive disclosure outperforms context dumping.** Feeding an agent everything at once degrades output quality. The PARA structure supports tiered loading naturally: root context first, project detail on demand. Skills follow the same pattern: the agent sees only a short description until it chooses to invoke one. Lean context up front, drill as needed. There is some evidence that too many context files can be detrimental to the agent's performance, so also be cautious on what you are feeding the agent on each session and how you write them (here is a useful guide to writing good `CLAUDE.md` files).

**Low-friction onboarding drives viral adoption.** The bootstrap `/para-init` command, which scans recent activity and builds an initial workspace, removed the biggest barrier to adoption. Users saw real value in their first session, without spending hours organizing files manually. When the entry cost is low enough, people share it with their team the same day.

**Your users are your best builders.** Every major feature after the initial launch (laptop support, Google Drive optimization, automated meeting note processing, visual reporting, shared team context) was built by community members, not the original author. Over a dozen contributors shipped code. Hundreds more wrote guides, answered questions, and created custom skills. Grassroots growth to 60K+ installs happened without any top-down directive.

**Composability creates more value than features.** Because skills are public markdown files and the workspace is just a filesystem, anyone can extend the system for their own needs. Teams built skills for sprint check-ins, performance review tracking, client health dashboards, and data analysis workflows. The plugin stopped being a tool and became a platform, and each extension made the whole system more useful for everyone.

## What's Next

The project has moved beyond personal productivity. A team-level shared context system, internally called **"Third Brain"**, lets team members' individual workspaces feed into a shared knowledge layer, and is now piloting across dozens of teams with hundreds of participants. Proactive agents run on schedules rather than waiting for prompts: morning briefings, automated meeting note processing, end-of-day digests. And the underlying architecture is converging with Meta's broader AI platforms, bringing structured context and persistent memory to more tools and more users.

What started as one data scientist's fix for scattered notes became a company-wide experiment in how humans and AI agents can work together on sustained, complex knowledge work.
