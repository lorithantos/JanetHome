---
name: deploy
description: Deploy a project to Azure using the JanetHome manifest-driven orchestrator. Use when the user asks to deploy, push a build, ship to staging/production, set up deployment for a new project, or troubleshoot a failing deploy.
---

# Manifest-driven deploy

This skill is a pointer, not a store. The pipeline is code, the project specifics
are manifests, and the accumulated deployment knowledge lives in the JanetHome
research graph — query it instead of re-deriving or trusting this file's age.

```powershell
$janet = if ($env:JanetBase) { $env:JanetBase } else { 'D:\Repos\JanetHome' }
```

## Invariants (the only content that belongs here)

1. Never write a bespoke deploy script. Projects carry a `deploy-manifest.json`;
   the generic orchestrator is `$janet\scripts\Invoke-BuildDeploy.ps1`.
2. Always dry-run the contract first: `... -ManifestPath .\deploy-manifest.json -Validate`
   (reports every problem at once, executes nothing, shows the build stamp).
3. The orchestrator prints the promote/slot-swap command but never runs it.
   Promotion is a human decision — keep it that way.
4. "Deploy succeeded" describes the artifact, not the app. The health poll is the
   real result; a persistent 500 after deploy means investigate the app/platform,
   not the pipeline — and redeploying will not fix a platform-side cause.

## Everything else: query the graph

```powershell
& $janet\scripts\Get-Research.ps1 -Id script.invoke-build-deploy -Expand   # orchestrator + manifest schema + caveats
& $janet\scripts\Get-Research.ps1 -Tag azure                               # Azure failure modes (site extensions, slot swaps, Kudu access)
& $janet\scripts\Get-Research.ps1 -Query 'deploy'                          # anything newer than this file
```

Read the `caveats` arrays — they are the "what bites you" channel. New deployment
lessons go into the graph (`Add-ResearchNode.ps1` / `Update-ResearchNode.ps1`),
NOT into this file.
