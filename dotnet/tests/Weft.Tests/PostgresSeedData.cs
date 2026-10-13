namespace Weft.Tests;

internal sealed class PostgresSeedData
{
    public Guid OrganizationA { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public Guid OrganizationB { get; } = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public Guid Alice { get; } = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public Guid Bob { get; } = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public Guid Admitted { get; } = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public Guid OtherProjectAdmin { get; } = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public Guid ProjectA { get; } = Guid.Parse("00000001-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public Guid HiddenProjectA { get; } = Guid.Parse("00000002-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public Guid ProjectB { get; } = Guid.Parse("00000001-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public static async Task<PostgresSeedData> CreateAsync(PostgresCase test)
    {
        var seed = new PostgresSeedData();
        await test.ExecuteAsync("INSERT INTO weft.organizations(id,name) VALUES ($1,$2),($3,$4)",
            seed.OrganizationA, "Organization A", seed.OrganizationB, "Organization B");
        foreach (var principal in new[] { seed.Alice, seed.Bob, seed.Admitted, seed.OtherProjectAdmin })
            await test.ExecuteAsync("INSERT INTO weft.principals(id,active) VALUES ($1,true)", principal);
        foreach (var (organization, principal) in new[]
        {
            (seed.OrganizationA, seed.Alice), (seed.OrganizationA, seed.Admitted),
            (seed.OrganizationA, seed.OtherProjectAdmin), (seed.OrganizationB, seed.Alice),
            (seed.OrganizationB, seed.Bob)
        })
            await test.ExecuteAsync("INSERT INTO weft.organization_memberships(organization_id,principal_id,active) VALUES ($1,$2,true)", organization, principal);
        await seed.AddProjectAsync(test, seed.OrganizationA, seed.ProjectA, seed.Alice, "Project A", "A description");
        await seed.AddProjectAsync(test, seed.OrganizationA, seed.HiddenProjectA, seed.Alice, "Hidden project", "");
        await seed.AddProjectAsync(test, seed.OrganizationB, seed.ProjectB, seed.Bob, "Project B", "B description");
        await seed.AddMembershipAsync(test, seed.OrganizationA, seed.ProjectA, seed.Alice, "viewer");
        await seed.AddMembershipAsync(test, seed.OrganizationA, seed.HiddenProjectA, seed.OtherProjectAdmin, "administrator");
        await seed.AddMembershipAsync(test, seed.OrganizationB, seed.ProjectB, seed.Alice, "runner");
        return seed;
    }

    public Task AddProjectAsync(PostgresCase test, Guid organization, Guid project, Guid creator, string name, string description) =>
        test.ExecuteAsync("INSERT INTO weft.projects(organization_id,id,name,description,creator_principal_id) VALUES ($1,$2,$3,$4,$5)",
            organization, project, name, description, creator);

    public Task AddMembershipAsync(PostgresCase test, Guid organization, Guid project, Guid principal, string role) =>
        test.ExecuteAsync("INSERT INTO weft.project_memberships(organization_id,project_id,principal_id,role,active) VALUES ($1,$2,$3,$4,true)",
            organization, project, principal, role);
}
