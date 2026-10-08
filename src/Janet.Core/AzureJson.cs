using System.Text;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// Serializes the Azure envelopes -- what-if, deployment, role assignments, storage probe -- and
/// renders each for a terminal.
/// </summary>
/// <remarks>
/// Every key is always present; a value that does not apply is null rather than absent, so a
/// reader can tell "not applicable" from "this version does not report it". Errors keep ARM's
/// nesting (code, message, target, details) instead of being flattened into a sentence.
/// </remarks>
public static class AzureJson
{
    public static string Serialize(WhatIfResult result, bool pretty = false)
    {
        JsonObject summary = [];
        foreach ((string type, int count) in result.Summary)
        {
            summary[type] = count;
        }

        JsonObject root = new()
        {
            ["contract"] = result.Contract,
            ["path"] = result.Path,
            ["subscription"] = Subscription(result.Subscription),
            ["resourceGroup"] = result.ResourceGroup,
            ["deploymentName"] = result.DeploymentName,
            ["outcome"] = result.Outcome,
            ["diagnostics"] = BicepJson.Diagnostics(result.Diagnostics),
            ["summary"] = summary,
            ["unresolved"] = result.Unresolved,
            ["includesUnchanged"] = result.IncludesUnchanged,
            ["changes"] = new JsonArray([.. result.Changes.Select(c => (JsonNode)new JsonObject
            {
                ["changeType"] = c.ChangeType,
                ["resource"] = c.Resource,
                ["resourceId"] = c.ResourceId,
                ["unresolved"] = c.Unresolved,
                ["deltas"] = new JsonArray([.. c.Deltas.Select(d => (JsonNode)new JsonObject
                {
                    ["path"] = d.Path,
                    ["change"] = d.Change,
                    ["before"] = d.Before,
                    ["after"] = d.After,
                    ["unresolved"] = d.Unresolved,
                })]),
                ["fields"] = new JsonArray([.. c.Fields.Select(f => (JsonNode)new JsonObject
                {
                    ["path"] = f.Path,
                    ["value"] = f.Value,
                    ["unresolved"] = f.Unresolved,
                })]),
                ["fieldsTruncated"] = c.FieldsTruncated,
                ["unsupportedReason"] = c.UnsupportedReason,
            })]),
            ["error"] = Error(result.Error),
            ["durationSeconds"] = result.DurationSeconds,
        };

        return root.ToJsonString(pretty ? BicepJson.Indented : BicepJson.Compact);
    }

    public static string Serialize(DeploymentResult result, bool pretty = false)
    {
        JsonObject root = new()
        {
            ["contract"] = result.Contract,
            ["status"] = result.Status,
            ["outcome"] = result.Outcome,
            ["path"] = result.Path,
            ["subscription"] = Subscription(result.Subscription),
            ["resourceGroup"] = result.ResourceGroup,
            ["name"] = result.Name,
            ["provisioningState"] = result.ProvisioningState,
            ["timestamp"] = result.Timestamp,
            ["duration"] = result.Duration,
            ["correlationId"] = result.CorrelationId,
            ["diagnostics"] = BicepJson.Diagnostics(result.Diagnostics),
            ["outputs"] = result.Outputs.DeepClone(),
            ["error"] = Error(result.Error),
            ["failedOperations"] = new JsonArray([.. result.FailedOperations.Select(f => (JsonNode)new JsonObject
            {
                ["resourceType"] = f.ResourceType,
                ["resourceName"] = f.ResourceName,
                ["error"] = Error(f.Error),
            })]),
        };

        return root.ToJsonString(pretty ? BicepJson.Indented : BicepJson.Compact);
    }

    public static string Serialize(RoleAssignmentsResult result, bool pretty = false)
    {
        JsonObject root = new()
        {
            ["contract"] = result.Contract,
            ["operation"] = result.Operation,
            ["subscription"] = Subscription(result.Subscription),
            ["scope"] = result.Scope,
            ["assignee"] = result.Assignee,
            ["outcome"] = result.Outcome,
            ["principalNames"] = result.PrincipalNames,
            ["count"] = result.Assignments.Count,
            ["assignments"] = new JsonArray([.. result.Assignments.Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.Id,
                ["scope"] = a.Scope,
                ["relation"] = a.Relation,
                ["role"] = a.Role,
                ["roleType"] = a.RoleType,
                ["roleDefinitionId"] = a.RoleDefinitionId,
                ["principalId"] = a.PrincipalId,
                ["principalType"] = a.PrincipalType,
                ["principalName"] = a.PrincipalName,
                ["description"] = a.Description,
                ["createdOn"] = a.CreatedOn,
                ["createdBy"] = a.CreatedBy,
            })]),
        };

        return root.ToJsonString(pretty ? BicepJson.Indented : BicepJson.Compact);
    }

    public static string Serialize(StorageProbeResult result, bool pretty = false)
    {
        StorageAccountSettings s = result.Settings;

        JsonObject summary = [];
        foreach ((string status, int count) in result.Summary)
        {
            summary[status] = count;
        }

        JsonObject root = new()
        {
            ["contract"] = result.Contract,
            ["account"] = result.Account,
            ["subscription"] = Subscription(result.Subscription),
            ["caller"] = new JsonObject
            {
                ["objectId"] = result.Caller.ObjectId,
                ["name"] = result.Caller.Name,
            },
            ["settings"] = new JsonObject
            {
                ["id"] = s.Id,
                ["resourceGroup"] = s.ResourceGroup,
                ["location"] = s.Location,
                ["kind"] = s.Kind,
                ["sku"] = s.Sku,
                ["provisioningState"] = s.ProvisioningState,
                ["statusOfPrimary"] = s.StatusOfPrimary,
                ["allowSharedKeyAccess"] = s.AllowSharedKeyAccess,
                ["publicNetworkAccess"] = s.PublicNetworkAccess,
                ["networkDefaultAction"] = s.NetworkDefaultAction,
                ["networkBypass"] = s.NetworkBypass,
                ["ipRules"] = s.IpRules,
                ["virtualNetworkRules"] = s.VirtualNetworkRules,
                ["privateEndpoints"] = s.PrivateEndpoints,
                ["minimumTlsVersion"] = s.MinimumTlsVersion,
                ["blobEndpoint"] = s.BlobEndpoint,
                ["tableEndpoint"] = s.TableEndpoint,
            },
            ["summary"] = summary,
            ["targets"] = new JsonArray([.. result.Targets.Select(t => (JsonNode)new JsonObject
            {
                ["service"] = t.Service,
                ["name"] = t.Name,
                ["status"] = t.Status,
                ["httpStatus"] = t.HttpStatus,
                ["errorCode"] = t.ErrorCode,
                ["message"] = t.Message,
                ["itemsSeen"] = t.ItemsSeen,
                ["more"] = t.More,
            })]),
        };

        return root.ToJsonString(pretty ? BicepJson.Indented : BicepJson.Compact);
    }

    internal static JsonObject Subscription(AzureSubscription subscription) => new()
    {
        ["id"] = subscription.Id,
        ["name"] = subscription.Name,
        ["source"] = subscription.Source,
    };

    internal static JsonObject? Error(AzureError? error) => error is null
        ? null
        : new JsonObject
        {
            ["code"] = error.Code,
            ["message"] = error.Message,
            ["target"] = error.Target,
            ["details"] = new JsonArray([.. error.Details.Select(d => (JsonNode?)Error(d))]),
        };

    public static string Render(WhatIfResult result)
    {
        StringBuilder text = new();
        text.AppendLine($"what-if {result.Outcome}  {result.Path} -> {result.ResourceGroup} ({result.Subscription.Name ?? result.Subscription.Id})");
        text.AppendLine("  " + string.Join("  ", result.Summary.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value}")) +
                        (result.Unresolved > 0 ? $"   ({result.Unresolved} listed with unevaluated expressions)" : string.Empty));
        BicepJson.AppendDiagnostics(text, result.Diagnostics);
        AppendError(text, result.Error, "  ");

        foreach (WhatIfChange change in result.Changes)
        {
            text.AppendLine($"  {change.ChangeType,-11} {change.Resource}{(change.Unresolved ? "   [unevaluated]" : string.Empty)}");

            foreach (WhatIfDelta delta in change.Deltas)
            {
                text.AppendLine($"      {delta.Change,-8} {delta.Path}: {delta.Before ?? "-"} => {delta.After ?? "-"}{(delta.Unresolved ? "  [unevaluated]" : string.Empty)}");
            }

            foreach (WhatIfField field in change.Fields)
            {
                text.AppendLine($"      {field.Path} = {field.Value}");
            }
        }

        return text.ToString();
    }

    public static string Render(DeploymentResult result)
    {
        StringBuilder text = new();
        text.AppendLine($"deployment {result.Name}: {result.Outcome} ({result.Status}, {result.ProvisioningState ?? "not started"})  {result.ResourceGroup}");

        if (result.Duration is not null)
        {
            text.AppendLine($"  duration {result.Duration}  correlation {result.CorrelationId}");
        }

        BicepJson.AppendDiagnostics(text, result.Diagnostics);
        AppendError(text, result.Error, "  ");

        foreach (DeploymentFailure failure in result.FailedOperations)
        {
            text.AppendLine($"  failed: {failure.ResourceType}/{failure.ResourceName}");
            AppendError(text, failure.Error, "    ");
        }

        foreach ((string name, JsonNode? value) in result.Outputs)
        {
            text.AppendLine($"  output {name} = {value?.ToJsonString()}");
        }

        if (result.Status == "running")
        {
            text.AppendLine($"  still running -- poll with azure_deployment, resourceGroup {result.ResourceGroup}, name {result.Name}");
        }

        return text.ToString();
    }

    public static string Render(RoleAssignmentsResult result)
    {
        StringBuilder text = new();
        text.AppendLine($"role {result.Operation}{(result.Outcome is null ? string.Empty : $": {result.Outcome}")}  at {result.Scope}  (names {result.PrincipalNames})");

        foreach (RoleAssignmentRow row in result.Assignments)
        {
            text.AppendLine($"  {row.Relation,-9} {row.Role ?? row.RoleDefinitionId}  ->  {row.PrincipalName ?? row.PrincipalId} ({row.PrincipalType})");
            if (row.Relation != "at")
            {
                text.AppendLine($"            at {row.Scope}");
            }
        }

        return text.ToString();
    }

    public static string Render(StorageProbeResult result)
    {
        StorageAccountSettings s = result.Settings;
        StringBuilder text = new();
        text.AppendLine($"storage {result.Account} as {result.Caller.Name ?? result.Caller.ObjectId ?? "(unknown caller)"}");
        text.AppendLine($"  {s.ProvisioningState}, primary {s.StatusOfPrimary}; shared keys {(s.AllowSharedKeyAccess is false ? "off" : "ON")}; " +
                        $"public network {s.PublicNetworkAccess ?? "(default: Enabled)"}; firewall default {s.NetworkDefaultAction}, " +
                        $"{s.IpRules} ip rule(s), {s.VirtualNetworkRules} vnet rule(s), {s.PrivateEndpoints} private endpoint(s)");
        text.AppendLine("  " + string.Join("  ", result.Summary.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value}")));

        foreach (StorageTarget target in result.Targets)
        {
            string detail = target.Status == "ok"
                ? $"{target.ItemsSeen}{(target.More == true ? "+" : string.Empty)} item(s) seen"
                : $"{target.HttpStatus} {target.ErrorCode} {target.Message}".Trim();
            text.AppendLine($"  {target.Service,-5} {target.Name,-24} {target.Status,-11} {detail}");
        }

        return text.ToString();
    }

    private static void AppendError(StringBuilder text, AzureError? error, string indent)
    {
        if (error is not null)
        {
            text.AppendLine(indent + AzureErrors.Describe(error, string.Empty).Replace(Environment.NewLine, Environment.NewLine + indent));
        }
    }
}
