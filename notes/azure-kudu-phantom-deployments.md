# Azure App Service: phantom deployments (success reported, nothing deployed)

Incident writeup, 2026-07-28, `LtImageSelector` staging slot. Companion to
`azure-appservice-site-extension-version-mismatch.md` — the same app, a different,
longer-lived failure that the extension crash was masking.

## Symptom

`az webapp deploy --type zip` (OneDeploy) reports **Deployment successful**, the
deployment appears in Kudu's history with status 4, the site restarts and serves
HTTP 200 — and **no file in wwwroot changes**. The app keeps serving a build from
months ago. Every deploy since 2026-03-08 had been a no-op; nobody noticed because
the health check only proves *an* app runs, not *which* app.

## Root cause

App settings contained the fossil `WEBSITE_NODE_DEFAULT_VERSION = 6.9.1` (a
2016-era default that survives forever). Kudu's deployment pipeline generates and
runs its deployment script with that Node version. Node 6 cannot load the modern
tooling (first casualty: `util.promisify` used by the Application Insights node
agent, injected because `XDT_MicrosoftApplicationInsights_NodeJS = 1`), so the
script generation step crashes instantly.

The two deploy engines handle that crash differently:

- **OneDeploy** (`az webapp deploy`): swallows the crash, logs "Clean deploying to
  C:\home\site\wwwroot" and "Deployment successful" — total server-side time ~1.4s
  for a 107 MB zip. **Silent no-op.**
- **Classic zipdeploy** (`az webapp deployment source config-zip`): fails loudly
  with `provisioningState: Failed` and the Node stack trace in the nested
  deployment log. Deprecated, but honest.

## The tell

A "successful" deployment whose server-side duration is physically impossible for
the payload (seconds for 100+ MB), and `GET /api/vfs/site/wwwroot/` file mtimes
that predate the deployment. Trust neither the CLI exit code nor the deployment
status — **verify content**: a file mtime/size, or probe a route that only the new
build has (a missing route 404s; an `[Authorize]` route that exists redirects to
login with 200).

## Diagnosis path that worked

1. Deployed app lacks a new endpoint (404) despite successful deploys → suspect stale content.
2. `GET /api/vfs/site/wwwroot/` → DLL mtimes months old. Production's too.
3. Deployment log (`/api/deployments/<id>/log`) shows 1.4s "clean deploy" → impossible.
4. Ruled out run-from-package: `data/SitePackages/packagename.txt` existed (March
   GitHub Actions residue) but `WEBSITE_RUN_FROM_PACKAGE` unset and a VFS write to
   wwwroot succeeded → not a read-only mount, residue inert.
5. Switched to classic zipdeploy → same failure, now loud, with the Node 6 stack
   trace in the nested log (`details_url` of the "Generating deployment script" entry).

## Fix

On the slot: `WEBSITE_NODE_DEFAULT_VERSION=~20` and
`XDT_MicrosoftApplicationInsights_NodeJS=disabled` (a .NET app needs neither the
old pin nor node instrumentation). Redeploy → real extraction (real warmup 503s),
file sizes change, new routes appear.

## Residue to remember

- Old `SitePackages/*.zip` + `packagename.txt` from the GitHub Actions era sit on
  both slots. Inert while `WEBSITE_RUN_FROM_PACKAGE` is unset, but confusing to
  every future investigation — delete when convenient.
- Fossil app settings never die on long-lived App Services. When deployment
  behaves impossibly, read the full `az webapp config appsettings list` output and
  ask "what decade is each of these from?"
