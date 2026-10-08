using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Role assignments listed with names resolved, assigned idempotently and removed by id,
/// against canned ARM and Microsoft Graph answers.
/// </summary>
public class RoleAssignmentsTests
{
    private const string Contributor = "ba92f5b4-2d11-453d-a403-e96b0029c9fe";
    private const string Reader = "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1";
    private const string Owner = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";

    private const string Person = "11111111-1111-4111-8111-111111111111";
    private const string Team = "22222222-2222-4222-8222-222222222222";
    /// <summary>Hex letters on purpose: the tests upper-case it to prove ids are normalised, which an all-digit GUID would not exercise.</summary>
    private const string App = "3a3b3c3d-3e3f-4333-8333-3333333333ab";

    private static readonly string SubId = $"/subscriptions/{StubAzure.Sub}";
    private static readonly string Rg = $"{SubId}/resourceGroups/rg-neelam";
    private static readonly string Account = $"{Rg}/providers/Microsoft.Storage/storageAccounts/stneelam";

    private static string RoleId(string guid) => $"{SubId}/providers/Microsoft.Authorization/roleDefinitions/{guid}";

    private static JsonObject Assignment(string name, string scope, string role, string principal, string type) => new()
    {
        ["id"] = $"{scope}/providers/Microsoft.Authorization/roleAssignments/{name}",
        ["name"] = name,
        ["properties"] = new JsonObject
        {
            ["scope"] = scope,
            ["roleDefinitionId"] = RoleId(role),
            ["principalId"] = principal,
            ["principalType"] = type,
            ["createdOn"] = "2026-10-01T09:00:00Z",
        },
    };

    private static Func<HttpResponseMessage> Page(params JsonObject[] items) =>
        StubAzure.Json(200, new JsonObject { ["value"] = new JsonArray([.. items]) });

    private static Func<HttpResponseMessage> Known(params (string Id, string Key, string Name)[] found) =>
        Page([.. found.Select(f => new JsonObject { ["id"] = f.Id, [f.Key] = f.Name })]);

    /// <summary>The role definitions every operation reads to name roles, and a directory that knows everyone.</summary>
    private static StubAzure WithNames(Func<HttpResponseMessage>? graph = null) => new StubAzure()
        .On(HttpMethod.Get, "/providers/Microsoft.Authorization/roleDefinitions?", Page(
            new JsonObject { ["name"] = Contributor, ["properties"] = new JsonObject { ["roleName"] = "Storage Blob Data Contributor", ["type"] = "BuiltInRole" } },
            new JsonObject { ["name"] = Reader, ["properties"] = new JsonObject { ["roleName"] = "Storage Blob Data Reader", ["type"] = "BuiltInRole" } },
            new JsonObject { ["name"] = Owner, ["properties"] = new JsonObject { ["roleName"] = "Owner", ["type"] = "BuiltInRole" } }))
        .On(HttpMethod.Post, "graph.microsoft.com/v1.0/directoryObjects/getByIds", graph ?? Known(
            (Person, "userPrincipalName", "ayesha@example.invalid"),
            (Team, "displayName", "Neelam Staff"),
            (App, "displayName", "app-neelam")));

    // ---- naming ----------------------------------------------------------------------------

    [Fact]
    public void AnAssignmentNameIsStableForTheSameTriple()
    {
        Assert.Equal(
            RoleAssignments.AssignmentName(Rg, RoleId(Reader), App),
            RoleAssignments.AssignmentName(Rg, RoleId(Reader), App));
    }

    [Fact]
    public void AnAssignmentNameIsAVersionFiveGuid()
    {
        string name = RoleAssignments.AssignmentName(Rg, RoleId(Reader), App);

        // Version nibble 5, RFC 4122 variant (10xx): what makes it a well-formed name-based GUID.
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", name);
    }

    [Fact]
    public void AnAssignmentNameIgnoresCaseTrailingSlashesAndHowTheRoleIsSpelt()
    {
        string expected = RoleAssignments.AssignmentName(Rg, RoleId(Reader), App);

        Assert.Equal(expected, RoleAssignments.AssignmentName(Rg.ToUpperInvariant() + "/", RoleId(Reader).ToUpperInvariant(), App.ToUpperInvariant()));
        Assert.Equal(expected, RoleAssignments.AssignmentName(Rg, Reader, App));
    }

    [Fact]
    public void AnAssignmentNameChangesWithEachPartOfTheTriple()
    {
        string name = RoleAssignments.AssignmentName(Rg, RoleId(Reader), App);

        Assert.NotEqual(name, RoleAssignments.AssignmentName(Account, RoleId(Reader), App));
        Assert.NotEqual(name, RoleAssignments.AssignmentName(Rg, RoleId(Contributor), App));
        Assert.NotEqual(name, RoleAssignments.AssignmentName(Rg, RoleId(Reader), Person));
    }

    [Theory]
    [InlineData("/subscriptions/S/resourceGroups/rg", "/subscriptions/s/RESOURCEGROUPS/RG/", "at")]
    [InlineData("/subscriptions/S/resourceGroups/rg", "/subscriptions/S/resourcegroups/rg/providers/Microsoft.Storage/storageAccounts/st", "below")]
    [InlineData("/subscriptions/S/resourceGroups/rg", "/subscriptions/S", "inherited")]
    [InlineData("/subscriptions/S/resourceGroups/rg", "/providers/Microsoft.Management/managementGroups/root", "inherited")]
    [InlineData("/subscriptions/S/resourceGroups/rg", "/", "inherited")]
    [InlineData("/subscriptions/S/resourceGroups/rg", "/subscriptions/S/resourceGroups/rg/", "at")]
    public void ARelationComparesScopesCaseInsensitively(string asked, string assigned, string expected)
    {
        Assert.Equal(expected, RoleAssignments.Relation(asked, assigned));
    }

    // ---- list ------------------------------------------------------------------------------

    [Fact]
    public void AListNamesRolesAndPrincipalsAndPutsTheNearestFirst()
    {
        // ARM spells the same resource group two ways; the "at" row arrives as resourcegroups/RG-NEELAM.
        StubAzure stub = WithNames().On(HttpMethod.Get, "/providers/Microsoft.Authorization/roleAssignments?", Page(
            Assignment("below1", Account, Reader, App, "ServicePrincipal"),
            Assignment("above1", SubId, Owner, Team, "Group"),
            Assignment("at1", $"{SubId}/resourcegroups/RG-NEELAM", Contributor, Person, "User")));

        RoleAssignmentsResult result = new RoleAssignments(stub.Http()).List(Rg);

        Assert.Equal("list", result.Operation);
        Assert.Null(result.Outcome);
        Assert.Null(result.Assignee);
        Assert.Equal(Rg, result.Scope);
        Assert.Equal(new AzureSubscription(StubAzure.Sub, null, "scope"), result.Subscription);
        Assert.Equal("resolved", result.PrincipalNames);

        Assert.Equal(["at", "inherited", "below"], result.Assignments.Select(a => a.Relation));
        Assert.Equal(["Storage Blob Data Contributor", "Owner", "Storage Blob Data Reader"], result.Assignments.Select(a => a.Role));
        Assert.Equal(["ayesha@example.invalid", "Neelam Staff", "app-neelam"], result.Assignments.Select(a => a.PrincipalName));
        Assert.All(result.Assignments, a => Assert.Equal("BuiltInRole", a.RoleType));

        // One directory call for every principal, with a Graph token.
        JsonNode body = JsonNode.Parse(Assert.Single(stub.Sent(HttpMethod.Post, "getByIds")).Body!)!;
        Assert.Equal(3, body["ids"]!.AsArray().Count);
        Assert.Contains("graph", stub.TokenScopes);
    }

    [Fact]
    public void AListForOnePrincipalFiltersOnTheServer()
    {
        StubAzure stub = WithNames().On(HttpMethod.Get, "/providers/Microsoft.Authorization/roleAssignments?", Page(
            Assignment("at1", Rg, Reader, App, "ServicePrincipal")));

        RoleAssignmentsResult result = new RoleAssignments(stub.Http()).List(Rg, App.ToUpperInvariant());

        Assert.Equal(App, result.Assignee);
        Assert.Contains(
            $"$filter=principalId%20eq%20%27{App}%27",
            Assert.Single(stub.Sent(HttpMethod.Get, "/roleAssignments?")).Url);
    }

    [Fact]
    public void AListWhenTheDirectoryRefusesStillAnswersAndSaysWhyNamesAreMissing()
    {
        StubAzure stub = WithNames(StubAzure.ArmError(403, "Authorization_RequestDenied", "Insufficient privileges to complete the operation."))
            .On(HttpMethod.Get, "/providers/Microsoft.Authorization/roleAssignments?", Page(
                Assignment("at1", Rg, Reader, App, "ServicePrincipal")));

        RoleAssignmentsResult result = new RoleAssignments(stub.Http()).List(Rg);

        Assert.Equal("unavailable: Graph answered HTTP 403 Authorization_RequestDenied", result.PrincipalNames);
        RoleAssignmentRow row = Assert.Single(result.Assignments);
        Assert.Null(row.PrincipalName);
        Assert.Equal("Storage Blob Data Reader", row.Role);
    }

    [Fact]
    public void AListWhereTheDirectoryKnowsOnlySomePrincipalsIsPartial()
    {
        StubAzure stub = WithNames(Known((App, "displayName", "app-neelam")))
            .On(HttpMethod.Get, "/providers/Microsoft.Authorization/roleAssignments?", Page(
                Assignment("at1", Rg, Reader, App, "ServicePrincipal"),
                Assignment("at2", Rg, Owner, Person, "User")));

        RoleAssignmentsResult result = new RoleAssignments(stub.Http()).List(Rg);

        Assert.Equal("partial", result.PrincipalNames);
    }

    // ---- assign ----------------------------------------------------------------------------

    private static RoleAssignRequest AssignReader() => new()
    {
        Scope = Rg + "/",
        Role = "storage blob data reader",
        Assignee = App.ToUpperInvariant(),
        PrincipalType = "serviceprincipal",
    };

    private static readonly string DerivedName = RoleAssignments.AssignmentName(Rg, RoleId(Reader), App);

    [Fact]
    public void AnAssignmentAlreadyMadeUnderTheDerivedNameExists()
    {
        StubAzure stub = WithNames().On(HttpMethod.Get, $"/roleAssignments/{DerivedName}?", StubAzure.Json(200,
            Assignment(DerivedName, Rg, Reader, App, "ServicePrincipal")));

        RoleAssignmentsResult result = new RoleAssignments(stub.Http()).Assign(AssignReader());

        Assert.Equal("assign", result.Operation);
        Assert.Equal("exists", result.Outcome);
        Assert.Equal(App, result.Assignee);
        Assert.Equal(Rg, result.Scope);
        Assert.Equal("at", Assert.Single(result.Assignments).Relation);
        Assert.Empty(stub.Sent(HttpMethod.Put, "/roleAssignments/"));
    }

    [Fact]
    public void ANewAssignmentIsCreatedUnderTheDerivedNameWithThePrincipalTypeSpeltAsArmSpellsIt()
    {
        StubAzure stub = WithNames()
            .On(HttpMethod.Get, $"/roleAssignments/{DerivedName}?", StubAzure.ArmError(404, "RoleAssignmentNotFound", "not found"))
            .On(HttpMethod.Put, $"/roleAssignments/{DerivedName}?", StubAzure.Json(201, Assignment(DerivedName, Rg, Reader, App, "ServicePrincipal")));

        RoleAssignmentsResult result = new RoleAssignments(stub.Http()).Assign(AssignReader());

        Assert.Equal("created", result.Outcome);

        JsonNode body = JsonNode.Parse(Assert.Single(stub.Sent(HttpMethod.Put, "/roleAssignments/")).Body!)!["properties"]!;
        Assert.Equal("ServicePrincipal", body["principalType"]!.GetValue<string>());
        Assert.Equal(RoleId(Reader), body["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal(App, body["principalId"]!.GetValue<string>());
    }

    [Fact]
    public void AnEquivalentAssignmentUnderAnotherNameIsFoundThroughRoleAssignmentExists()
    {
        StubAzure stub = WithNames()
            .On(HttpMethod.Get, $"/roleAssignments/{DerivedName}?", StubAzure.ArmError(404, "RoleAssignmentNotFound", "not found"))
            .On(HttpMethod.Put, $"/roleAssignments/{DerivedName}?", StubAzure.ArmError(409, "RoleAssignmentExists", "The role assignment already exists."))
            .On(HttpMethod.Get, "/roleAssignments?", Page(
                Assignment("elsewhere", Account, Reader, App, "ServicePrincipal"),
                Assignment("other-role", Rg, Owner, App, "ServicePrincipal"),
                Assignment("random-name", $"{SubId}/resourcegroups/RG-NEELAM", Reader.ToUpperInvariant(), App, "ServicePrincipal")));

        RoleAssignmentsResult result = new RoleAssignments(stub.Http()).Assign(AssignReader());

        Assert.Equal("exists", result.Outcome);
        Assert.EndsWith("/roleAssignments/random-name", Assert.Single(result.Assignments).Id);
    }

    [Fact]
    public void AnyOtherRefusalToAssignIsThrown()
    {
        StubAzure stub = WithNames()
            .On(HttpMethod.Get, $"/roleAssignments/{DerivedName}?", StubAzure.ArmError(404, "RoleAssignmentNotFound", "not found"))
            .On(HttpMethod.Put, $"/roleAssignments/{DerivedName}?", StubAzure.ArmError(400, "PrincipalNotFound", "Principal does not exist in the directory."));

        GraphException ex = Assert.Throws<GraphException>(() => new RoleAssignments(stub.Http()).Assign(AssignReader()));

        Assert.Contains("PrincipalNotFound", ex.Message);
        Assert.Contains("replicated", ex.Message);
    }

    // ---- remove ----------------------------------------------------------------------------

    private static readonly string AssignmentId = $"{Rg}/providers/Microsoft.Authorization/roleAssignments/r1";

    [Fact]
    public void RemovingAnAssignmentThatIsNotThereIsAbsentAndDeletesNothing()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Get, "/roleAssignments/r1?", StubAzure.ArmError(404, "RoleAssignmentNotFound", "not found"));

        RoleAssignmentsResult result = new RoleAssignments(stub.Http()).Remove(AssignmentId);

        Assert.Equal("remove", result.Operation);
        Assert.Equal("absent", result.Outcome);
        Assert.Equal("none", result.PrincipalNames);
        Assert.Equal(Rg, result.Scope);
        Assert.Null(result.Assignee);
        Assert.Empty(result.Assignments);
        Assert.Empty(stub.Sent(HttpMethod.Delete, "/roleAssignments/"));
    }

    [Fact]
    public void RemovingAnAssignmentDeletesItAndSaysWhatItWas()
    {
        StubAzure stub = WithNames()
            .On(HttpMethod.Get, "/roleAssignments/r1?", StubAzure.Json(200, Assignment("r1", Rg, Reader, App, "ServicePrincipal")))
            .On(HttpMethod.Delete, "/roleAssignments/r1?", StubAzure.Json(200, Assignment("r1", Rg, Reader, App, "ServicePrincipal")));

        RoleAssignmentsResult result = new RoleAssignments(stub.Http()).Remove(AssignmentId);

        Assert.Equal("removed", result.Outcome);
        Assert.Equal(App, result.Assignee);
        RoleAssignmentRow row = Assert.Single(result.Assignments);
        Assert.Equal("Storage Blob Data Reader", row.Role);
        Assert.Equal("app-neelam", row.PrincipalName);
        Assert.Single(stub.Sent(HttpMethod.Delete, "/roleAssignments/r1?"));
    }

    [Fact]
    public void RemovingSomethingThatIsNotAnAssignmentIdIsRefused()
    {
        Assert.Throws<GraphException>(() => new RoleAssignments(new StubAzure().Http()).Remove(Rg));
    }
}
