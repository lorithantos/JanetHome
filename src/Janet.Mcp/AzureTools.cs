using System.ComponentModel;
using Janet.Core;
using ModelContextProtocol.Server;

namespace Janet.Mcp;

/// <summary>
/// Azure, natively: deployments, role assignments and storage reachability over Resource Manager
/// and the data plane, with the token from the machine's `az login`.
/// </summary>
/// <remarks>
/// READS AND WRITES ARE SEPARATE TOOLS ON PURPOSE. A client's permission prompt is per tool, so
/// keeping what-if, status, listing and probing apart from deploy, assign and remove is what lets
/// the reads be allow-listed while every change to live Azure is still asked about, one at a
/// time. A single tool with a mode switch would have to be allowed or prompted as a whole.
///
/// Outcomes a caller acts on -- a template that does not compile, a rejected what-if, a failed
/// deployment -- come back in the envelope as fields. Only a request that cannot be attempted
/// (no such file, no sign-in, an id that is not an id) is refused with a message.
/// </remarks>
[McpServerToolType]
public static class AzureTools
{
    [McpServerTool(Name = "azure_whatif")]
    [Description(
        "READ-ONLY. What deploying a Bicep file to a resource group would change, from Azure " +
        "Resource Manager's what-if, without changing anything. Compiles the .bicepparam (or " +
        ".bicep) locally first; a file that does not compile comes back outcome 'notCompiled' " +
        "with its diagnostics, and a what-if ARM refuses comes back 'rejected' with ARM's nested " +
        "error. 'summary' counts every resource by change type; 'changes' lists the ones that " +
        "change (pass includeUnchanged for all), each with its property deltas flattened to " +
        "paths, or for a create or delete the resource's own properties. UNEVALUATED " +
        "EXPRESSIONS ARE FLAGGED, NOT HIDDEN: a value what-if could not evaluate -- typically a " +
        "reference() to a site's identity -- is marked unresolved:true, and ARM reports such a " +
        "property as modified whether or not anything will change, so read those as 'cannot " +
        "tell' rather than as changes. Subscription defaults to the Azure CLI's default one.")]
    public static string WhatIf(
        [Description("A .bicepparam (which names its template) or a .bicep file.")]
        string path,
        [Description("Resource group to compare against.")]
        string resourceGroup,
        [Description("Subscription id or name. Omit for the Azure CLI's default subscription.")]
        string? subscription = null,
        [Description("Parameter overrides as name=value; a value that parses as JSON is taken as JSON, anything else as a string.")]
        string[]? overrides = null,
        [Description("Also list resources with no change and ignored ones. They are always counted in 'summary'.")]
        bool includeUnchanged = false) =>
        AzureJson.Serialize(AzureDeployments.Shared.WhatIf(
            new DeploymentRequest
            {
                Path = path,
                ResourceGroup = resourceGroup,
                Subscription = subscription,
                Overrides = overrides ?? [],
            },
            includeUnchanged));

    [McpServerTool(Name = "azure_deploy")]
    [Description(
        "CHANGES LIVE AZURE. Deploys a Bicep file to a resource group in Incremental mode " +
        "(Complete mode, which deletes what the template omits, is deliberately not offered). " +
        "Run azure_whatif first and read it. THE RESPONSE IS A TAGGED UNION: 'status' " +
        "\"complete\" carries the outcome -- deployed, failed (with ARM's error and each failed " +
        "operation's resource and reason in 'failedOperations'), canceled, notCompiled (with " +
        "Bicep's diagnostics) or rejected (refused before it started, with ARM's validation " +
        "error) -- while \"running\" means it outlasted the wait: poll it with azure_deployment " +
        "using the returned 'name'. Template outputs come back in 'outputs'.")]
    public static string Deploy(
        [Description("A .bicepparam (which names its template) or a .bicep file.")]
        string path,
        [Description("Resource group to deploy into. It must already exist.")]
        string resourceGroup,
        [Description("Subscription id or name. Omit for the Azure CLI's default subscription.")]
        string? subscription = null,
        [Description("Parameter overrides as name=value; a value that parses as JSON is taken as JSON, anything else as a string.")]
        string[]? overrides = null,
        [Description("Deployment name. Omit to derive one from the file name and the UTC time, so deployments never overwrite each other's history.")]
        string? name = null,
        [Description("Seconds to wait for completion before answering 'running'. Default 50.")]
        int waitSeconds = 50) =>
        AzureJson.Serialize(AzureDeployments.Shared.Deploy(
            new DeploymentRequest
            {
                Path = path,
                ResourceGroup = resourceGroup,
                Subscription = subscription,
                Name = name,
                Overrides = overrides ?? [],
            },
            TimeSpan.FromSeconds(Math.Max(0, waitSeconds))));

    [McpServerTool(Name = "azure_deployment")]
    [Description(
        "READ-ONLY. The state of a resource-group deployment by name: the poll target for an " +
        "azure_deploy that answered 'running', and a way to read any past deployment. Same " +
        "envelope as azure_deploy, with 'path' null since nothing was compiled. A failed " +
        "deployment carries each failed operation's resource and ARM's reason.")]
    public static string Deployment(
        [Description("Resource group the deployment ran in.")]
        string resourceGroup,
        [Description("Deployment name, as azure_deploy returned it.")]
        string name,
        [Description("Subscription id or name. Omit for the Azure CLI's default subscription.")]
        string? subscription = null) =>
        AzureJson.Serialize(AzureDeployments.Shared.Status(resourceGroup, name, subscription));

    [McpServerTool(Name = "azure_role_list")]
    [Description(
        "READ-ONLY. Role assignments in force at, above and below a scope, each with 'relation' " +
        "(at / inherited / below), the role's name and the principal's name resolved -- not " +
        "GUIDs. Scope comparison is case-insensitive, because Azure spells the same resource " +
        "group /resourceGroups/ in one API and /resourcegroups/ in another, and a " +
        "case-sensitive filter reports nothing assigned when something is. 'principalNames' " +
        "says whether the names could be read from the directory (a guest account often cannot); " +
        "a null name with 'unavailable' means unknown, not nameless. Narrow with assignee.")]
    public static string RoleList(
        [Description("ARM id of a subscription, resource group or resource. Omit for the default subscription.")]
        string? scope = null,
        [Description("Object id of one principal, to see only what is granted to it -- including what it holds below the scope.")]
        string? assignee = null,
        [Description("Subscription id or name, used only when scope is omitted.")]
        string? subscription = null) =>
        AzureJson.Serialize(RoleAssignments.Shared.List(scope, assignee, subscription));

    [McpServerTool(Name = "azure_role_assign")]
    [Description(
        "CHANGES LIVE AZURE. Grants a role to a principal at a scope. IDEMPOTENT: the assignment " +
        "name is derived from (scope, role, principal), so asking twice reports outcome 'exists' " +
        "rather than failing or duplicating, and an equivalent assignment made elsewhere under " +
        "another name is found and reported the same way. The answer echoes the assignment with " +
        "role and principal named.")]
    public static string RoleAssign(
        [Description("ARM id the role applies at: a subscription, resource group or resource.")]
        string scope,
        [Description("Role name (e.g. 'Storage Blob Data Reader'), role GUID, or role definition id.")]
        string role,
        [Description("Object id of the user, group or service principal.")]
        string assignee,
        [Description("User, Group, ServicePrincipal or ForeignGroup. Required: without it a principal created moments ago fails with PrincipalNotFound.")]
        string principalType,
        [Description("Why this grant exists. Shown wherever the assignment is listed.")]
        string? description = null) =>
        AzureJson.Serialize(RoleAssignments.Shared.Assign(new RoleAssignRequest
        {
            Scope = scope,
            Role = role,
            Assignee = assignee,
            PrincipalType = principalType,
            Description = description,
        }));

    [McpServerTool(Name = "azure_role_remove")]
    [Description(
        "CHANGES LIVE AZURE. Removes one role assignment by its full id (azure_role_list shows " +
        "each one's). Reads it first and echoes what was removed -- role, principal, scope -- " +
        "rather than an id nobody can read. An id that no longer exists is outcome 'absent', " +
        "not an error: the state asked for already holds.")]
    public static string RoleRemove(
        [Description("Full role assignment id: /subscriptions/.../providers/Microsoft.Authorization/roleAssignments/<guid>.")]
        string id) =>
        AzureJson.Serialize(RoleAssignments.Shared.Remove(id));

    [McpServerTool(Name = "storage_probe")]
    [Description(
        "READ-ONLY. Can THIS machine, signed in as THIS person, actually reach this storage " +
        "account's data? Reports the account's access settings (shared keys, public network " +
        "access, firewall default action and rule counts, private endpoints) from Resource " +
        "Manager, then READS each container and table over the data plane with an Entra token " +
        "and reports per target: ok, forbidden (errorCode AuthorizationPermissionMismatch = no " +
        "data role; AuthorizationFailure = usually a network rule), notFound, or unreachable (no " +
        "HTTP answer at all). Subscription Owner does not imply data access, which is why the " +
        "data plane is actually read. COUNTS ONLY: at most 5 items are read per target and only " +
        "their number is returned, never a blob name or entity, so it is safe against an account " +
        "holding someone else's data. 'caller' names whose access was tested.")]
    public static string StorageProbe(
        [Description("Storage account name.")]
        string account,
        [Description("Its resource group. Optional; saves listing the subscription's accounts.")]
        string? resourceGroup = null,
        [Description("Subscription id or name. Omit for the Azure CLI's default subscription.")]
        string? subscription = null,
        [Description("Containers to probe. Omit for every container the account has.")]
        string[]? containers = null,
        [Description("Tables to probe. Omit for every table the account has.")]
        string[]? tables = null) =>
        AzureJson.Serialize(Janet.Core.StorageProbe.Shared.Probe(new StorageProbeRequest
        {
            Account = account,
            ResourceGroup = resourceGroup,
            Subscription = subscription,
            Containers = containers ?? [],
            Tables = tables ?? [],
        }));
}
