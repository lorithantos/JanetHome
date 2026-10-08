using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>One role assignment, with its role and principal named rather than left as GUIDs.</summary>
/// <param name="Relation">
/// at: assigned at the scope asked about. inherited: assigned ABOVE it (a parent resource group,
/// the subscription, a management group) and so in force there. below: assigned to something
/// inside the scope.
/// </param>
/// <param name="Role">The role's name, or null when its definition could not be read.</param>
/// <param name="RoleType">BuiltInRole or CustomRole, or null with Role.</param>
/// <param name="PrincipalName">A user's sign-in name, or a group's or service principal's display name; null when the directory could not be asked.</param>
public sealed record RoleAssignmentRow(
    string Id,
    string Scope,
    string Relation,
    string? Role,
    string? RoleType,
    string RoleDefinitionId,
    string PrincipalId,
    string? PrincipalType,
    string? PrincipalName,
    string? Description,
    string? CreatedOn,
    string? CreatedBy);

/// <summary>The answer to a list, an assign or a remove: one envelope, so a caller reads all three the same way.</summary>
public sealed record RoleAssignmentsResult
{
    /// <summary>Format version. See contracts\azure-roles.schema.json.</summary>
    public const int ContractVersion = 1;

    public int Contract => ContractVersion;

    /// <summary>list, assign or remove.</summary>
    public required string Operation { get; init; }

    public required AzureSubscription Subscription { get; init; }

    public required string Scope { get; init; }

    /// <summary>The principal a list was narrowed to, or the one assigned; null otherwise.</summary>
    public required string? Assignee { get; init; }

    /// <summary>null for a list. created or exists for an assign; removed or absent for a remove.</summary>
    public required string? Outcome { get; init; }

    /// <summary>resolved, partial, none (nothing to resolve) or "unavailable: why". Whether PrincipalName can be trusted to be filled in.</summary>
    public required string PrincipalNames { get; init; }

    public required IReadOnlyList<RoleAssignmentRow> Assignments { get; init; }
}

/// <summary>What to assign.</summary>
public sealed class RoleAssignRequest
{
    /// <summary>The ARM id the role applies at: a subscription, resource group or resource.</summary>
    public required string Scope { get; init; }

    /// <summary>A role name ("Storage Blob Data Reader"), a role GUID, or a role definition id.</summary>
    public required string Role { get; init; }

    /// <summary>The principal's object id.</summary>
    public required string Assignee { get; init; }

    /// <summary>User, Group, ServicePrincipal or ForeignGroup.</summary>
    /// <remarks>
    /// Required rather than looked up. ARM accepts an assignment without it, but then has to
    /// find the principal itself, and a principal created moments ago is not yet replicated
    /// where ARM looks -- the assignment fails with PrincipalNotFound. Saying the type is
    /// Microsoft's documented fix, and the caller always knows it.
    /// </remarks>
    public required string PrincipalType { get; init; }

    public string? Description { get; init; }
}

/// <summary>
/// Azure role assignments: listed with names resolved, assigned idempotently, removed by id.
/// </summary>
/// <remarks>
/// SCOPE COMPARISON IS CASE-INSENSITIVE, because Azure's is and its spelling is not consistent:
/// the same resource group comes back as /resourceGroups/ from one API and /resourcegroups/ from
/// another. A client-side filter comparing case-sensitively finds nothing and reports, falsely,
/// that nothing is assigned -- which is how `az role assignment list --query` misled a session.
/// </remarks>
public sealed class RoleAssignments(AzureHttp http)
{
    public static RoleAssignments Shared { get; } = new(AzureHttp.Shared);

    private static readonly string[] PrincipalTypes = ["User", "Group", "ServicePrincipal", "ForeignGroup"];

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, (string Name, string Type)>> _roles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every assignment in force at, above or below a scope, optionally for one principal.
    /// </summary>
    /// <param name="scope">An ARM id. Null means the subscription.</param>
    public RoleAssignmentsResult List(string? scope = null, string? assignee = null, string? subscriptionRequested = null)
    {
        AzureSubscription subscription = SubscriptionFor(scope, subscriptionRequested);
        string at = Normalise(scope ?? $"/subscriptions/{subscription.Id}");
        string? principal = string.IsNullOrWhiteSpace(assignee) ? null : RequireGuid(assignee, "assignee");

        // Unfiltered, ARM answers with assignments at, above AND below the scope. The principal
        // filter keeps all three directions; atScope() would drop "below", which is the answer
        // to "what in this resource group has been granted to whom".
        string filter = principal is null ? string.Empty : $"&$filter={Uri.EscapeDataString($"principalId eq '{principal}'")}";
        IReadOnlyList<JsonObject> raw = http.ArmList(
            $"{at}/providers/Microsoft.Authorization/roleAssignments?api-version={AzureHttp.AuthorizationApi}{filter}",
            $"list role assignments at {at}");

        (IReadOnlyList<RoleAssignmentRow> rows, string names) = Rows(raw, at, subscription);

        return new RoleAssignmentsResult
        {
            Operation = "list",
            Subscription = subscription,
            Scope = at,
            Assignee = principal,
            Outcome = null,
            PrincipalNames = names,
            Assignments = rows,
        };
    }

    /// <summary>
    /// Assigns a role, or reports the assignment that already does the same thing.
    /// </summary>
    /// <remarks>
    /// IDEMPOTENT BY NAME. The assignment's name is derived from (scope, role, principal), so
    /// asking twice finds the first one rather than failing or duplicating. An equivalent
    /// assignment made elsewhere under a random name -- `az role assignment create` uses one --
    /// is found too, through ARM's RoleAssignmentExists refusal, and reported as "exists".
    /// </remarks>
    public RoleAssignmentsResult Assign(RoleAssignRequest request)
    {
        AzureSubscription subscription = SubscriptionFor(request.Scope, null);
        string scope = Normalise(request.Scope);
        string principal = RequireGuid(request.Assignee, "assignee");
        string principalType = PrincipalTypes.FirstOrDefault(t => t.Equals(request.PrincipalType, StringComparison.OrdinalIgnoreCase))
            ?? throw new GraphException($"principalType '{request.PrincipalType}' is not one of {string.Join(", ", PrincipalTypes)}.");
        string roleDefinitionId = RoleDefinitionId(request.Role, subscription);
        string name = AssignmentName(scope, roleDefinitionId, principal);
        string id = $"{scope}/providers/Microsoft.Authorization/roleAssignments/{name}";

        AzureResponse existing = http.Arm(HttpMethod.Get, $"{id}?api-version={AzureHttp.AuthorizationApi}");
        if (existing.IsSuccess)
        {
            return Single("assign", "exists", subscription, scope, principal, existing.Json()!);
        }

        JsonObject body = new()
        {
            ["properties"] = new JsonObject
            {
                ["roleDefinitionId"] = roleDefinitionId,
                ["principalId"] = principal,
                ["principalType"] = principalType,
                ["description"] = request.Description,
            },
        };

        AzureResponse created = http.Arm(HttpMethod.Put, $"{id}?api-version={AzureHttp.AuthorizationApi}", body);

        if (created.IsSuccess)
        {
            return Single("assign", "created", subscription, scope, principal, created.Json()!);
        }

        AzureError error = created.Error()!;

        if (error.Code == "RoleAssignmentExists")
        {
            JsonObject? equivalent = http.ArmList(
                    $"{scope}/providers/Microsoft.Authorization/roleAssignments?api-version={AzureHttp.AuthorizationApi}&$filter={Uri.EscapeDataString($"principalId eq '{principal}'")}",
                    $"find the existing assignment at {scope}")
                .FirstOrDefault(a =>
                    string.Equals(Normalise(a["properties"]?["scope"]?.GetValue<string>() ?? string.Empty), scope, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a["properties"]?["roleDefinitionId"]?.GetValue<string>(), roleDefinitionId, StringComparison.OrdinalIgnoreCase));

            if (equivalent is not null)
            {
                return Single("assign", "exists", subscription, scope, principal, equivalent);
            }
        }

        throw new GraphException(
            $"Azure refused the role assignment (HTTP {created.Status}).{Environment.NewLine}{AzureErrors.Describe(error)}" +
            (error.Code == "PrincipalNotFound"
                ? $"{Environment.NewLine}If the principal was created moments ago, it may not have replicated yet; retry in a minute."
                : string.Empty));
    }

    /// <summary>Removes one assignment by its id, and says what it was.</summary>
    /// <remarks>
    /// Read before deleting so the answer can name what was removed -- role, principal, scope --
    /// rather than echo back an id nobody can read. A missing assignment is "absent", not an
    /// error: the state the caller wanted already holds.
    /// </remarks>
    public RoleAssignmentsResult Remove(string assignmentId)
    {
        if (string.IsNullOrWhiteSpace(assignmentId)
            || !assignmentId.Contains("/providers/Microsoft.Authorization/roleAssignments/", StringComparison.OrdinalIgnoreCase))
        {
            throw new GraphException(
                $"'{assignmentId}' is not a role assignment id. It looks like " +
                "/subscriptions/<sub>/.../providers/Microsoft.Authorization/roleAssignments/<guid>; azure_role_list shows each one's id.");
        }

        string id = assignmentId.Trim();
        string scope = Normalise(id[..id.IndexOf("/providers/Microsoft.Authorization/roleAssignments/", StringComparison.OrdinalIgnoreCase)]);
        AzureSubscription subscription = SubscriptionFor(id, null);

        AzureResponse existing = http.Arm(HttpMethod.Get, $"{id}?api-version={AzureHttp.AuthorizationApi}");

        if (existing.Status == 404)
        {
            return new RoleAssignmentsResult
            {
                Operation = "remove",
                Subscription = subscription,
                Scope = scope,
                Assignee = null,
                Outcome = "absent",
                PrincipalNames = "none",
                Assignments = [],
            };
        }

        if (!existing.IsSuccess)
        {
            throw new GraphException($"Azure refused to read {id} (HTTP {existing.Status}).{Environment.NewLine}{AzureErrors.Describe(existing.Error()!)}");
        }

        JsonObject assignment = existing.Json()!;
        AzureResponse deleted = http.Arm(HttpMethod.Delete, $"{id}?api-version={AzureHttp.AuthorizationApi}");

        if (!deleted.IsSuccess && deleted.Status != 404)
        {
            throw new GraphException($"Azure refused to remove {id} (HTTP {deleted.Status}).{Environment.NewLine}{AzureErrors.Describe(deleted.Error()!)}");
        }

        return Single("remove", "removed", subscription, scope, assignment["properties"]?["principalId"]?.GetValue<string>(), assignment);
    }

    /// <summary>
    /// The assignment name for (scope, role, principal): a name-based GUID, so the same triple
    /// always produces the same name.
    /// </summary>
    /// <remarks>
    /// RFC 4122 version-5 layout over SHA-256 rather than SHA-1: the version bits are what make
    /// it a well-formed GUID, and nothing here depends on interoperating with another
    /// implementation's v5 output -- only on being stable. Exposed for the tests.
    /// </remarks>
    public static string AssignmentName(string scope, string roleDefinitionId, string principalId)
    {
        string role = roleDefinitionId[(roleDefinitionId.LastIndexOf('/') + 1)..];
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Normalise(scope).ToLowerInvariant()}|{role.ToLowerInvariant()}|{principalId.ToLowerInvariant()}"));

        byte[] bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true).ToString();
    }

    /// <summary>at, inherited or below: where an assignment's scope sits relative to the one asked about.</summary>
    public static string Relation(string asked, string assigned)
    {
        string a = Normalise(asked);
        string b = Normalise(assigned);

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return "at";
        }

        if (b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase))
        {
            return "below";
        }

        // A prefix of the asked scope, or a management group or the root ("/"), which sit
        // above every subscription without being a path prefix of it.
        return "inherited";
    }

    private RoleAssignmentsResult Single(string operation, string outcome, AzureSubscription subscription, string scope, string? principal, JsonObject assignment)
    {
        (IReadOnlyList<RoleAssignmentRow> rows, string names) = Rows([assignment], scope, subscription);

        return new RoleAssignmentsResult
        {
            Operation = operation,
            Subscription = subscription,
            Scope = scope,
            Assignee = principal,
            Outcome = outcome,
            PrincipalNames = names,
            Assignments = rows,
        };
    }

    private (IReadOnlyList<RoleAssignmentRow> Rows, string Names) Rows(IReadOnlyList<JsonObject> raw, string asked, AzureSubscription subscription)
    {
        IReadOnlyDictionary<string, (string Name, string Type)> roles = RoleNames(subscription);
        (IReadOnlyDictionary<string, string> names, string state) = PrincipalNames(
            [.. raw.Select(a => a["properties"]?["principalId"]?.GetValue<string>()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)]);

        List<RoleAssignmentRow> rows = [];

        foreach (JsonObject assignment in raw)
        {
            JsonNode? p = assignment["properties"];
            string roleDefinitionId = p?["roleDefinitionId"]?.GetValue<string>() ?? string.Empty;
            string roleGuid = roleDefinitionId[(roleDefinitionId.LastIndexOf('/') + 1)..];
            string principalId = p?["principalId"]?.GetValue<string>() ?? string.Empty;
            string scope = p?["scope"]?.GetValue<string>() ?? string.Empty;
            bool knownRole = roles.TryGetValue(roleGuid, out (string Name, string Type) role);

            rows.Add(new RoleAssignmentRow(
                assignment["id"]?.GetValue<string>() ?? string.Empty,
                scope,
                Relation(asked, scope),
                knownRole ? role.Name : null,
                knownRole ? role.Type : null,
                roleDefinitionId,
                principalId,
                p?["principalType"]?.GetValue<string>(),
                names.TryGetValue(principalId, out string? principalName) ? principalName : null,
                p?["description"]?.GetValue<string>(),
                p?["createdOn"]?.GetValue<string>(),
                p?["createdBy"]?.GetValue<string>()));
        }

        // Nearest first: what is assigned right here, then what is inherited, then what lies below.
        return ([.. rows.OrderBy(r => r.Relation switch { "at" => 0, "inherited" => 1, _ => 2 })
                        .ThenBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(r => r.Role, StringComparer.OrdinalIgnoreCase)], state);
    }

    /// <summary>Role GUID to (name, type), built-in and custom, cached per subscription for the process.</summary>
    private IReadOnlyDictionary<string, (string Name, string Type)> RoleNames(AzureSubscription subscription) =>
        _roles.GetOrAdd(subscription.Id, id => http
            .ArmList($"/subscriptions/{id}/providers/Microsoft.Authorization/roleDefinitions?api-version={AzureHttp.AuthorizationApi}", "list role definitions")
            .Where(d => d["name"] is not null)
            .GroupBy(d => d["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (g.First()["properties"]?["roleName"]?.GetValue<string>() ?? g.Key,
                      g.First()["properties"]?["type"]?.GetValue<string>() ?? "(unknown)"),
                StringComparer.OrdinalIgnoreCase));

    private string RoleDefinitionId(string role, AzureSubscription subscription)
    {
        string wanted = role.Trim();

        if (wanted.StartsWith('/'))
        {
            return wanted;
        }

        string? guid = Guid.TryParse(wanted, out Guid parsed)
            ? parsed.ToString()
            : RoleNames(subscription).FirstOrDefault(r => r.Value.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).Key;

        if (guid is null)
        {
            string[] near = [.. RoleNames(subscription).Values
                .Select(r => r.Name)
                .Where(n => wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word => n.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(8)];

            throw new GraphException(
                $"No role named '{wanted}' in subscription {subscription.Id}." +
                (near.Length > 0 ? $" Close: {string.Join("; ", near)}." : string.Empty));
        }

        return $"/subscriptions/{subscription.Id}/providers/Microsoft.Authorization/roleDefinitions/{guid}";
    }

    /// <summary>
    /// Principal ids to readable names through Microsoft Graph, or a reason it could not be done.
    /// </summary>
    /// <remarks>
    /// Degrades rather than fails. Reading the directory needs a permission ARM does not -- a
    /// guest account often lacks it -- and an assignment list without names is still the answer
    /// to the question asked. The state says which happened, so a null name is never mistaken
    /// for a principal that has none.
    /// </remarks>
    private (IReadOnlyDictionary<string, string> Names, string State) PrincipalNames(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return (new Dictionary<string, string>(), "none");
        }

        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (string[] batch in ids.Chunk(1000))
            {
                JsonObject body = new()
                {
                    ["ids"] = new JsonArray([.. batch.Select(id => (JsonNode)id)]),
                    ["types"] = new JsonArray("user", "group", "servicePrincipal"),
                };

                AzureResponse response = http.Send(HttpMethod.Post, "https://graph.microsoft.com/v1.0/directoryObjects/getByIds", "graph", body);

                if (!response.IsSuccess)
                {
                    AzureError error = response.Error()!;
                    return (names, $"unavailable: Graph answered HTTP {response.Status} {error.Code}");
                }

                foreach (JsonObject item in (response.Json()?["value"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    string? id = item["id"]?.GetValue<string>();
                    string? name = item["userPrincipalName"]?.GetValue<string>() ?? item["displayName"]?.GetValue<string>();

                    if (id is not null && name is not null)
                    {
                        names[id] = name;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is GraphException or HttpRequestException or TaskCanceledException)
        {
            return (names, $"unavailable: {ex.Message.Split(Environment.NewLine)[0]}");
        }

        return (names, names.Count == ids.Count ? "resolved" : names.Count == 0 ? "unavailable: no principal found in the directory" : "partial");
    }

    private static AzureSubscription SubscriptionFor(string? scope, string? requested)
    {
        if (scope is not null && AzureHttp.Segment(scope, "subscriptions") is string fromScope)
        {
            return new AzureSubscription(fromScope.ToLowerInvariant(), null, "scope");
        }

        if (!string.IsNullOrWhiteSpace(scope) && !scope.TrimStart().StartsWith('/'))
        {
            throw new GraphException($"Scope '{scope}' is not an ARM id. Pass one starting /subscriptions/..., as azure_role_list or the portal shows it.");
        }

        return AzureSubscription.Resolve(requested);
    }

    private static string RequireGuid(string value, string what) =>
        Guid.TryParse(value.Trim(), out Guid parsed)
            ? parsed.ToString()
            : throw new GraphException($"{what} '{value}' is not an object id (a GUID). Look one up with 'az ad user show' or 'az ad sp show', or from azure_role_list.");

    private static string Normalise(string scope)
    {
        string trimmed = scope.Trim();
        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }
}
