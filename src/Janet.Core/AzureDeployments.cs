using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Janet.Core;

/// <summary>A resource-group deployment of a Bicep file: what to deploy, and where.</summary>
public sealed class DeploymentRequest
{
    /// <summary>A .bicepparam (which names its template) or a bare .bicep.</summary>
    public required string Path { get; init; }

    public required string ResourceGroup { get; init; }

    /// <summary>Subscription id or name. Null means the Azure CLI's default.</summary>
    public string? Subscription { get; init; }

    /// <summary>Deployment name. Null derives one from the file name and the time.</summary>
    public string? Name { get; init; }

    /// <summary>name=value pairs laid over the file's parameters. See <see cref="Bicep.Compile"/>.</summary>
    public IReadOnlyList<string> Overrides { get; init; } = [];
}

/// <summary>One property a what-if says will change, flattened to a path.</summary>
/// <param name="Change">create, delete, modify, array or noEffect -- ARM's propertyChangeType.</param>
/// <param name="Before">The value before, rendered compactly and capped; null when there was none.</param>
/// <param name="Unresolved">The after-value is an ARM expression the what-if could not evaluate.</param>
public sealed record WhatIfDelta(string Path, string Change, string? Before, string? After, bool Unresolved);

/// <summary>One property of a resource being created or deleted.</summary>
public sealed record WhatIfField(string Path, string Value, bool Unresolved);

/// <summary>One resource in a what-if.</summary>
/// <param name="Resource">The id relative to its resource group (provider/type/name), or the full id when it lies outside one.</param>
/// <param name="Unresolved">At least one value here is an expression the what-if could not evaluate -- typically a reference() to a resource it has not deployed. ARM reports those as modifications whether or not anything will change.</param>
/// <param name="Fields">For a create or a delete: the resource's own properties, scalars flattened to paths.</param>
public sealed record WhatIfChange(
    string ChangeType,
    string ResourceId,
    string Resource,
    bool Unresolved,
    IReadOnlyList<WhatIfDelta> Deltas,
    IReadOnlyList<WhatIfField> Fields,
    bool FieldsTruncated,
    string? UnsupportedReason);

/// <summary>What a deployment would change, without changing it.</summary>
public sealed record WhatIfResult
{
    /// <summary>Format version. See contracts\azure-whatif.schema.json.</summary>
    public const int ContractVersion = 1;

    public int Contract => ContractVersion;

    public required string Path { get; init; }

    public required AzureSubscription Subscription { get; init; }

    public required string ResourceGroup { get; init; }

    public required string DeploymentName { get; init; }

    /// <summary>compared, notCompiled (the Bicep did not build) or rejected (ARM refused the what-if).</summary>
    public required string Outcome { get; init; }

    /// <summary>What Bicep said while compiling, errors and warnings alike.</summary>
    public required IReadOnlyList<BicepDiagnostic> Diagnostics { get; init; }

    /// <summary>Count per change type, over every resource, including the ones not listed.</summary>
    public required IReadOnlyDictionary<string, int> Summary { get; init; }

    /// <summary>How many listed changes carry an unevaluated expression.</summary>
    public required int Unresolved { get; init; }

    /// <summary>Whether noChange and ignore resources are listed, or only counted.</summary>
    public required bool IncludesUnchanged { get; init; }

    public required IReadOnlyList<WhatIfChange> Changes { get; init; }

    public required AzureError? Error { get; init; }

    public required double DurationSeconds { get; init; }
}

/// <summary>A deployment operation that failed, with the resource it was working on.</summary>
public sealed record DeploymentFailure(string? ResourceType, string? ResourceName, AzureError Error);

/// <summary>A deployment's state: finished, or still running with the name to poll.</summary>
public sealed record DeploymentResult
{
    /// <summary>Format version. See contracts\azure-deployment.schema.json.</summary>
    public const int ContractVersion = 1;

    public int Contract => ContractVersion;

    /// <summary>complete or running. Running carries the name to pass to azure_deployment.</summary>
    public required string Status { get; init; }

    /// <summary>deployed, failed, canceled, running, notCompiled or rejected (refused before it started).</summary>
    public required string Outcome { get; init; }

    /// <summary>The file deployed, or null when this is a status read of a deployment by name.</summary>
    public required string? Path { get; init; }

    public required AzureSubscription Subscription { get; init; }

    public required string ResourceGroup { get; init; }

    public required string Name { get; init; }

    /// <summary>ARM's own provisioningState, verbatim; null when ARM never accepted the deployment.</summary>
    public required string? ProvisioningState { get; init; }

    public required string? Timestamp { get; init; }

    /// <summary>ISO 8601 duration as ARM reports it, e.g. PT41.2S.</summary>
    public required string? Duration { get; init; }

    public required string? CorrelationId { get; init; }

    public required IReadOnlyList<BicepDiagnostic> Diagnostics { get; init; }

    /// <summary>Template outputs, name to value. Empty until the deployment succeeds.</summary>
    public required JsonObject Outputs { get; init; }

    public required AzureError? Error { get; init; }

    public required IReadOnlyList<DeploymentFailure> FailedOperations { get; init; }
}

/// <summary>
/// Resource-group deployments of Bicep files, through Resource Manager: what-if, deploy, and
/// the state of a deployment by name.
/// </summary>
/// <remarks>
/// Every outcome a caller has to act on -- a template that does not compile, a what-if ARM
/// rejected, a deployment that failed -- comes back IN the envelope, with Bicep's diagnostics
/// and ARM's nested error details as fields. Only requests that cannot be attempted at all (no
/// such file, no sign-in, no default subscription) are refused as exceptions.
///
/// Incremental mode only. Complete mode deletes whatever in the resource group the template
/// does not mention, and that is not an option a tool should make one parameter away.
/// </remarks>
public sealed class AzureDeployments(AzureHttp http, Func<string, IReadOnlyList<string>, BicepCompiled>? compile = null)
{
    // Injectable so the tests exercise every ARM path without the Bicep CLI installed.
    private readonly Func<string, IReadOnlyList<string>, BicepCompiled> _compile =
        compile ?? ((path, overrides) => Bicep.Compile(path, overrides));

    /// <summary>How long a what-if may run before it is reported as unfinished.</summary>
    public static readonly TimeSpan WhatIfLimit = TimeSpan.FromMinutes(10);

    /// <summary>How long a deploy call waits for ARM before handing back a name to poll.</summary>
    /// <remarks>
    /// Under a client's patience for one tool call, and long enough for a deployment that only
    /// changes role assignments or settings to finish inside it.
    /// </remarks>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(50);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private static readonly string[] ChangeTypes = ["create", "delete", "modify", "deploy", "noChange", "ignore", "unsupported"];

    public static AzureDeployments Shared { get; } = new(AzureHttp.Shared);

    public WhatIfResult WhatIf(DeploymentRequest request, bool includeUnchanged = false)
    {
        Stopwatch clock = Stopwatch.StartNew();
        AzureSubscription subscription = AzureSubscription.Resolve(request.Subscription);
        string name = request.Name ?? DefaultName(request.Path);
        BicepCompiled compiled = _compile(request.Path, request.Overrides);

        WhatIfResult Result(string outcome, IReadOnlyList<JsonObject> changes, AzureError? error) =>
            Summarise(compiled, subscription, request.ResourceGroup, name, outcome, changes, includeUnchanged, error, clock);

        if (!compiled.Succeeded)
        {
            return Result("notCompiled", [], null);
        }

        JsonObject body = new()
        {
            ["properties"] = new JsonObject
            {
                ["mode"] = "Incremental",
                ["template"] = compiled.Template,
                ["parameters"] = compiled.Parameters,

                // Full payloads, not ResourceIdOnly: the deltas -- which property, from what to
                // what -- are the whole point, and the id-only form reports every resource as
                // a bare "Deploy" with nothing to tell a real change from a restatement.
                ["whatIfSettings"] = new JsonObject { ["resultFormat"] = "FullResourcePayloads" },
            },
        };

        AzureResponse accepted = http.Arm(HttpMethod.Post, $"{DeploymentPath(subscription, request.ResourceGroup, name)}/whatIf?api-version={AzureHttp.ResourcesApi}", body);

        if (!accepted.IsSuccess)
        {
            return Result("rejected", [], accepted.Error());
        }

        AzureResponse final = http.AwaitOperation(accepted, WhatIfLimit, "what-if");
        JsonObject? answer = final.Json();

        if (!final.IsSuccess || answer?["error"] is JsonObject || answer?["status"]?.GetValue<string>() is "Failed")
        {
            AzureError error = answer?["error"] is JsonObject failure ? AzureErrors.Read(failure) : final.Error() ?? new AzureError($"HTTP {final.Status}", final.Body, null, []);
            return Result("rejected", [], error);
        }

        IReadOnlyList<JsonObject> changes = [.. (answer?["properties"]?["changes"] as JsonArray ?? []).OfType<JsonObject>()];
        return Result("compared", changes, null);
    }

    /// <summary>
    /// Starts an incremental deployment and waits up to <paramref name="wait"/> for it.
    /// </summary>
    public DeploymentResult Deploy(DeploymentRequest request, TimeSpan? wait = null)
    {
        AzureSubscription subscription = AzureSubscription.Resolve(request.Subscription);
        string name = request.Name ?? DefaultName(request.Path);
        BicepCompiled compiled = _compile(request.Path, request.Overrides);

        if (!compiled.Succeeded)
        {
            return NotStarted(compiled, subscription, request.ResourceGroup, name, "notCompiled", null);
        }

        JsonObject body = new()
        {
            ["properties"] = new JsonObject
            {
                ["mode"] = "Incremental",
                ["template"] = compiled.Template,
                ["parameters"] = compiled.Parameters,
            },
        };

        string path = DeploymentPath(subscription, request.ResourceGroup, name);
        AzureResponse started = http.Arm(HttpMethod.Put, $"{path}?api-version={AzureHttp.ResourcesApi}", body);

        if (!started.IsSuccess)
        {
            // Refused before it began -- template validation, a policy, a quota. The details
            // name the resource and the rule, and they are the answer.
            return NotStarted(compiled, subscription, request.ResourceGroup, name, "rejected", started.Error());
        }

        TimeSpan limit = wait ?? DefaultWait;
        TimeSpan waited = TimeSpan.Zero;
        JsonObject deployment = started.Json() ?? [];

        while (!IsTerminal(deployment) && waited < limit)
        {
            http.Sleep(PollInterval);
            waited += PollInterval;
            deployment = http.ArmJson(HttpMethod.Get, $"{path}?api-version={AzureHttp.ResourcesApi}", $"read deployment '{name}'");
        }

        return Describe(deployment, subscription, request.ResourceGroup, name, compiled.Path, compiled.Diagnostics);
    }

    /// <summary>A deployment's state by name: the poll target for a deploy that came back running.</summary>
    public DeploymentResult Status(string resourceGroup, string name, string? subscriptionRequested = null)
    {
        AzureSubscription subscription = AzureSubscription.Resolve(subscriptionRequested);
        AzureResponse response = http.Arm(HttpMethod.Get, $"{DeploymentPath(subscription, resourceGroup, name)}?api-version={AzureHttp.ResourcesApi}");

        if (response.Status == 404)
        {
            throw new GraphException($"No deployment named '{name}' in resource group '{resourceGroup}' of subscription {subscription.Id}.");
        }

        if (!response.IsSuccess)
        {
            throw new GraphException($"Azure refused to read deployment '{name}' (HTTP {response.Status}).{Environment.NewLine}{AzureErrors.Describe(response.Error()!)}");
        }

        return Describe(response.Json() ?? [], subscription, resourceGroup, name, null, []);
    }

    private DeploymentResult Describe(
        JsonObject deployment,
        AzureSubscription subscription,
        string resourceGroup,
        string name,
        string? path,
        IReadOnlyList<BicepDiagnostic> diagnostics)
    {
        JsonNode? properties = deployment["properties"];
        string? state = properties?["provisioningState"]?.GetValue<string>();
        bool terminal = IsTerminal(deployment);

        JsonObject outputs = [];
        foreach ((string key, JsonNode? output) in properties?["outputs"] as JsonObject ?? [])
        {
            outputs[key] = output?["value"]?.DeepClone();
        }

        IReadOnlyList<DeploymentFailure> failures = state is "Failed"
            ? FailedOperations(subscription, resourceGroup, name)
            : [];

        return new DeploymentResult
        {
            Status = terminal ? "complete" : "running",
            Outcome = state switch
            {
                "Succeeded" => "deployed",
                "Failed" => "failed",
                "Canceled" => "canceled",
                _ => "running",
            },
            Path = path,
            Subscription = subscription,
            ResourceGroup = resourceGroup,
            Name = name,
            ProvisioningState = state,
            Timestamp = properties?["timestamp"]?.GetValue<string>(),
            Duration = properties?["duration"]?.GetValue<string>(),
            CorrelationId = properties?["correlationId"]?.GetValue<string>(),
            Diagnostics = diagnostics,
            Outputs = outputs,
            Error = properties?["error"] is JsonObject error ? AzureErrors.Read(error) : null,
            FailedOperations = failures,
        };
    }

    /// <summary>
    /// The operations that failed, with the resource each was working on. The deployment's own
    /// error is usually a wrapper ("At least one resource deployment operation failed"); this is
    /// where the actual reason lives.
    /// </summary>
    private IReadOnlyList<DeploymentFailure> FailedOperations(AzureSubscription subscription, string resourceGroup, string name)
    {
        IReadOnlyList<JsonObject> operations = http.ArmList(
            $"{DeploymentPath(subscription, resourceGroup, name)}/operations?api-version={AzureHttp.ResourcesApi}",
            $"list the operations of deployment '{name}'");

        return [.. operations
            .Select(operation => operation["properties"])
            .Where(properties => properties?["provisioningState"]?.GetValue<string>() == "Failed")
            .Select(properties => new DeploymentFailure(
                properties?["targetResource"]?["resourceType"]?.GetValue<string>(),
                properties?["targetResource"]?["resourceName"]?.GetValue<string>(),
                OperationError(properties)))];
    }

    /// <summary>
    /// A failed operation's reason, from whichever of the three shapes ARM used.
    /// </summary>
    /// <remarks>
    /// statusMessage is usually { error: { code, message, details } }, sometimes an object with
    /// only a status and a message, and sometimes a bare JSON string. Indexing a string node as an
    /// object throws, so the shape is matched first -- a failed deployment must come back as
    /// outcome "failed" with its reasons, never as an exception from reading them.
    /// </remarks>
    private static AzureError OperationError(JsonNode? properties)
    {
        string code = properties?["statusCode"]?.GetValue<string>() ?? "(no code)";

        return properties?["statusMessage"] switch
        {
            JsonObject { } message when message["error"] is JsonObject error => AzureErrors.Read(error),
            JsonObject message => new AzureError(code, message["message"]?.GetValue<string>() ?? message.ToJsonString(), null, []),
            JsonValue text when text.TryGetValue(out string? plain) => new AzureError(code, plain, null, []),
            JsonNode other => new AzureError(code, other.ToJsonString(), null, []),
            null => new AzureError(code, string.Empty, null, []),
        };
    }

    private static DeploymentResult NotStarted(
        BicepCompiled compiled,
        AzureSubscription subscription,
        string resourceGroup,
        string name,
        string outcome,
        AzureError? error) => new()
        {
            Status = "complete",
            Outcome = outcome,
            Path = compiled.Path,
            Subscription = subscription,
            ResourceGroup = resourceGroup,
            Name = name,
            ProvisioningState = null,
            Timestamp = null,
            Duration = null,
            CorrelationId = null,
            Diagnostics = compiled.Diagnostics,
            Outputs = [],
            Error = error,
            FailedOperations = [],
        };

    private static bool IsTerminal(JsonObject deployment) =>
        deployment["properties"]?["provisioningState"]?.GetValue<string>() is "Succeeded" or "Failed" or "Canceled";

    private static WhatIfResult Summarise(
        BicepCompiled compiled,
        AzureSubscription subscription,
        string resourceGroup,
        string name,
        string outcome,
        IReadOnlyList<JsonObject> raw,
        bool includeUnchanged,
        AzureError? error,
        Stopwatch clock)
    {
        List<WhatIfChange> all = [.. raw.Select(Change)];

        Dictionary<string, int> summary = ChangeTypes.ToDictionary(type => type, _ => 0, StringComparer.Ordinal);
        foreach (WhatIfChange change in all)
        {
            summary[change.ChangeType] = summary.GetValueOrDefault(change.ChangeType) + 1;
        }

        List<WhatIfChange> listed = [.. all.Where(change => includeUnchanged || change.ChangeType is not ("noChange" or "ignore"))];

        return new WhatIfResult
        {
            Path = compiled.Path,
            Subscription = subscription,
            ResourceGroup = resourceGroup,
            DeploymentName = name,
            Outcome = outcome,
            Diagnostics = compiled.Diagnostics,
            Summary = summary,
            Unresolved = listed.Count(change => change.Unresolved),
            IncludesUnchanged = includeUnchanged,
            Changes = listed,
            Error = error,
            DurationSeconds = Math.Round(clock.Elapsed.TotalSeconds, 2),
        };
    }

    /// <summary>One raw what-if change, flattened. Exposed for the tests, which feed it recorded ARM answers.</summary>
    public static WhatIfChange Change(JsonObject change)
    {
        string type = Camel(change["changeType"]?.GetValue<string>() ?? "unsupported");
        string id = change["resourceId"]?.GetValue<string>() ?? string.Empty;

        List<WhatIfDelta> deltas = [];
        foreach (JsonObject delta in (change["delta"] as JsonArray ?? []).OfType<JsonObject>())
        {
            FlattenDelta(delta, string.Empty, deltas);
        }

        // A create or delete has no delta: the whole resource is the change, so its properties
        // are what tell a role assignment for the right principal from one for the wrong one.
        JsonNode? whole = type switch
        {
            "create" => change["after"]?["properties"],
            "delete" => change["before"]?["properties"],
            _ => null,
        };

        List<WhatIfField> fields = [];
        if (whole is not null)
        {
            FlattenFields(whole, "properties", fields);
        }

        bool truncated = fields.Count > FieldCap;

        return new WhatIfChange(
            type,
            id,
            Relative(id),
            deltas.Any(d => d.Unresolved) || fields.Any(f => f.Unresolved),
            deltas,
            truncated ? fields[..FieldCap] : fields,
            truncated,
            change["unsupportedReason"]?.GetValue<string>());
    }

    /// <summary>How many properties of a created or deleted resource are listed before the rest are cut.</summary>
    public const int FieldCap = 25;

    /// <summary>How long a rendered value may be. Templates and policies embed whole documents.</summary>
    public const int ValueCap = 200;

    private static void FlattenDelta(JsonObject delta, string prefix, List<WhatIfDelta> into)
    {
        string segment = delta["path"]?.GetValue<string>() ?? string.Empty;
        string path = Join(prefix, segment);
        string change = Camel(delta["propertyChangeType"]?.GetValue<string>() ?? "modify");

        if (delta["children"] is JsonArray { Count: > 0 } children)
        {
            foreach (JsonObject child in children.OfType<JsonObject>())
            {
                FlattenDelta(child, path, into);
            }

            return;
        }

        JsonNode? after = delta["after"];
        into.Add(new WhatIfDelta(path, change, Render(delta["before"]), Render(after), IsExpression(after)));
    }

    private static void FlattenFields(JsonNode node, string path, List<WhatIfField> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? child) in obj)
                {
                    if (child is not null)
                    {
                        FlattenFields(child, Join(path, key), into);
                    }
                }

                break;

            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonNode item)
                    {
                        FlattenFields(item, $"{path}[{i}]", into);
                    }
                }

                break;

            default:
                into.Add(new WhatIfField(path, Render(node) ?? "null", IsExpression(node)));
                break;
        }
    }

    private static string Join(string prefix, string segment) =>
        prefix.Length == 0 ? segment
        : segment.Length == 0 ? prefix
        : int.TryParse(segment, out _) ? $"{prefix}[{segment}]"
        : $"{prefix}.{segment}";

    private static string? Render(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        string text = value is JsonValue scalar && scalar.TryGetValue(out string? s) ? s : value.ToJsonString();
        return text.Length > ValueCap ? text[..ValueCap] + "..." : text;
    }

    /// <summary>
    /// An ARM template expression the what-if could not evaluate: "[reference(...)]" and kin. A
    /// leading "[[" is ARM's escape for a literal bracket, so it is not one.
    /// </summary>
    private static bool IsExpression(JsonNode? value) =>
        value is JsonValue scalar
        && scalar.TryGetValue(out string? text)
        && text.Length > 1
        && text[0] == '['
        && text[^1] == ']'
        && !text.StartsWith("[[", StringComparison.Ordinal);

    private static string Camel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string Relative(string resourceId)
    {
        Match match = Regex.Match(resourceId, "/resourceGroups/[^/]+/providers/(.+)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : resourceId;
    }

    private static string DeploymentPath(AzureSubscription subscription, string resourceGroup, string name) =>
        $"/subscriptions/{subscription.Id}/resourcegroups/{Uri.EscapeDataString(resourceGroup)}/providers/Microsoft.Resources/deployments/{Uri.EscapeDataString(name)}";

    /// <summary>
    /// The file's name and the UTC time, in the characters ARM allows and within its 64.
    /// </summary>
    /// <remarks>
    /// Timestamped so two deployments of one file never overwrite each other's history -- the
    /// record of what was deployed when is the deployment list itself.
    /// </remarks>
    public static string DefaultName(string path)
    {
        string stem = Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(path), "[^A-Za-z0-9_.()-]", "-");
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return $"{(stem.Length > 48 ? stem[..48] : stem)}-{stamp}";
    }
}
