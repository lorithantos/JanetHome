# Azure App Service: site extension / runtime version mismatch kills app startup

Incident writeup, 2026-07-27, `LtImageSelector` staging slot (personal Azure, `Lori_Photography` resource group).

## Symptom

Every deploy "succeeds" (zip lands, OneDeploy reports `provisioningState: Succeeded`) but the site
returns **HTTP 500.30 — ASP.NET Core app failed to start**, forever. Event log shows the app process
dying before `Main` completes:

```
System.IO.FileNotFoundException: Could not load file or assembly
'Microsoft.Extensions.DependencyInjection.Abstractions, Version=10.0.0.0, ...'.
The system cannot find the file specified.
   at Program.<Main>$(String[] args)
```

The cruelty of this error: the assembly is a *shared framework* assembly. It is not supposed to be in
your deployment, it IS present in the framework folder on the worker, your `deps.json` is correct, and
the package layout is correct. Nothing you redeploy will ever fix it.

## Root cause

The **ASP.NET Core Logging Integration** site extension
(`Microsoft.AspNetCore.AzureAppServices.SiteExtension`) was installed at version **10.0.10**, but the
App Service worker only had ASP.NET Core runtimes **10.0.8 / 10.0.9** installed.

The extension injects `DOTNET_ADDITIONAL_DEPS` into every worker process. The host merges that
manifest with the resolved framework (10.0.9), prefers the extension's newer `Microsoft.Extensions.*`
entries, then looks for the corresponding assemblies in the extension's store — which only has them
for 10.0.10. Result: `FileNotFoundException` for a framework assembly, at JIT of `Program.Main`,
before any app code runs.

How it got installed: a Visual Studio publish that afternoon (the App Service logging option; note
the pubxml already said `InstallAspNetCoreSiteExtension=false`, but the VS dialog path installed it
anyway). Every deploy from that moment on crashed identically, which looked exactly like "my new
build broke the app."

## Diagnosis path that worked

1. `az webapp log download` (or Kudu VFS `GET /api/vfs/LogFiles/eventlog.xml`) → the real exception.
   The HTTP 500.30 page and `az webapp log tail` tell you almost nothing.
2. Rule out the app: `deps.json` in the zip has no reference to the "missing" assembly → it is
   expected from the shared framework.
3. Kudu `POST /api/command` with `dotnet --list-runtimes` — check the **64-bit** install explicitly
   (`"C:\Program Files\dotnet\dotnet.exe"`); the Kudu console is 32-bit and shows the x86 list.
4. The framework folder had the DLL → so something was *redirecting* resolution.
5. `GET /api/siteextensions?listInstalled=true` → extension version vs runtime version mismatch.

Kudu REST with AAD (basic auth disabled): `az rest --resource "https://management.core.windows.net/"`
against `https://<app>-<slot>.scm.azurewebsites.net/api/...`.

## Fix

Either works:

- **Uninstall** (durable; right answer when the app logs via OpenTelemetry and doesn't use the
  integration): `DELETE /api/siteextensions/Microsoft.AspNetCore.AzureAppServices.SiteExtension`,
  then restart.
- **Pin the matching version** (keeps the integration): uninstall first, then
  `PUT /api/siteextensions/<id>` with body `{"version": "10.0.9"}`. A PUT against an existing
  install returns the old version unchanged — **it will not downgrade in place**.

Then restart the slot and poll. Recheck after every future VS publish: the extension can silently
come back at "latest".

## Related trap cleared in the same session

The slot also carried the **legacy App Insights codeless attach agent**
(`ApplicationInsightsAgent_EXTENSION_VERSION=~2`, `XDT_MicrosoftApplicationInsights_Mode=recommended`)
— v2-era, redundant since the app moved to the OpenTelemetry SDK, and itself a known source of the
same class of startup injection failures on newer runtimes. Disabled. It was *not* the cause this
time, but it muddied the search space.
