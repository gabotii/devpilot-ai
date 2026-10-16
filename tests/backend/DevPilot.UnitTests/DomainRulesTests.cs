using DevPilot.Application.Common;
using DevPilot.Application.Projects;
using DevPilot.Domain.Common;
using DevPilot.Domain.Organizations;
using DevPilot.Domain.Projects;
using DevPilot.Domain.WorkItems;

namespace DevPilot.UnitTests;

public class OrganizationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Creator_becomes_owner()
    {
        var owner = Guid.NewGuid();
        var org = Organization.Create("Acme", "acme", owner, Now);
        Assert.Equal(OrgRole.Owner, Assert.Single(org.Members).Role);
    }

    [Fact]
    public void Last_owner_cannot_be_demoted_or_leave()
    {
        var owner = Guid.NewGuid();
        var org = Organization.Create("Acme", "acme", owner, Now);
        Assert.Equal("org.last_owner", Assert.Throws<DomainException>(() => org.ChangeRole(OrgRole.Owner, owner, OrgRole.Admin)).Code);
        Assert.Equal("org.last_owner", Assert.Throws<DomainException>(() => org.RemoveMember(OrgRole.Owner, owner, owner)).Code);
    }

    [Fact]
    public void Owner_can_be_demoted_when_another_owner_exists()
    {
        var owner = Guid.NewGuid();
        var second = Guid.NewGuid();
        var org = Organization.Create("Acme", "acme", owner, Now);
        org.AddMember(second, OrgRole.Developer, Now);
        org.ChangeRole(OrgRole.Owner, second, OrgRole.Owner);
        org.ChangeRole(OrgRole.Owner, owner, OrgRole.Admin);
        Assert.Equal(OrgRole.Admin, org.Members.Single(m => m.UserId == owner).Role);
    }

    [Theory]
    [InlineData(OrgRole.Developer, OrgRole.Admin)] // admins cannot grant admin
    [InlineData(OrgRole.Admin, OrgRole.Viewer)] // admins cannot manage other admins
    [InlineData(OrgRole.Owner, OrgRole.Developer)] // admins cannot manage owners
    public void Admin_cannot_manage_peers_or_grant_admin(OrgRole targetRole, OrgRole newRole)
    {
        var org = Organization.Create("Acme", "acme", Guid.NewGuid(), Now);
        var target = Guid.NewGuid();
        org.AddMember(target, targetRole, Now);
        Assert.Throws<ForbiddenDomainException>(() => org.ChangeRole(OrgRole.Admin, target, newRole));
    }

    [Fact]
    public void Admin_can_manage_developers_and_viewers()
    {
        var org = Organization.Create("Acme", "acme", Guid.NewGuid(), Now);
        var dev = Guid.NewGuid();
        org.AddMember(dev, OrgRole.Developer, Now);
        org.ChangeRole(OrgRole.Admin, dev, OrgRole.Viewer);
        org.RemoveMember(OrgRole.Admin, Guid.NewGuid(), dev);
        Assert.DoesNotContain(org.Members, m => m.UserId == dev);
    }

    [Theory]
    [InlineData(OrgRole.Developer, OrgRole.Viewer, false)]
    [InlineData(OrgRole.Admin, OrgRole.Developer, true)]
    [InlineData(OrgRole.Admin, OrgRole.Admin, false)]
    [InlineData(OrgRole.Owner, OrgRole.Admin, true)]
    public void Invite_permissions(OrgRole actor, OrgRole invited, bool expected) => Assert.Equal(expected, Organization.CanInvite(actor, invited));

    [Fact]
    public void Duplicate_member_is_rejected()
    {
        var owner = Guid.NewGuid();
        var org = Organization.Create("Acme", "acme", owner, Now);
        Assert.Equal("org.already_member", Assert.Throws<DomainException>(() => org.AddMember(owner, OrgRole.Viewer, Now)).Code);
    }
}

public class InvitationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Owner_role_cannot_be_invited() =>
        Assert.Throws<DomainException>(() => new Invitation(Guid.NewGuid(), "a@b.com", OrgRole.Owner, "h", Guid.NewGuid(), Now));

    [Fact]
    public void Accept_requires_matching_email_and_pending_status()
    {
        var invitation = new Invitation(Guid.NewGuid(), "Dev@Example.com", OrgRole.Developer, "h", Guid.NewGuid(), Now);
        Assert.Equal("invitation.email_mismatch", Assert.Throws<DomainException>(() => invitation.Accept(Guid.NewGuid(), "other@example.com", Now)).Code);

        invitation.Accept(Guid.NewGuid(), "dev@example.com", Now);
        Assert.Equal(InvitationStatus.Accepted, invitation.GetStatus(Now));
        Assert.Equal("invitation.not_pending", Assert.Throws<DomainException>(() => invitation.Revoke(Now)).Code);
    }

    [Fact]
    public void Invitation_expires_after_lifetime()
    {
        var invitation = new Invitation(Guid.NewGuid(), "dev@example.com", OrgRole.Viewer, "h", Guid.NewGuid(), Now);
        var later = Now + Invitation.Lifetime;
        Assert.Equal(InvitationStatus.Expired, invitation.GetStatus(later));
        Assert.Throws<DomainException>(() => invitation.Accept(Guid.NewGuid(), "dev@example.com", later));
    }
}

public class WorkItemRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly Project _project;
    private readonly Workflow _workflow;
    private readonly Dictionary<string, WorkflowStatus> _status;

    public WorkItemRulesTests()
    {
        _project = Project.Create(Guid.NewGuid(), "Payments", "PAY", null, Guid.NewGuid(), Now);
        var (statuses, transitions) = WorkflowTemplate.CreateDefault(_project.OrganizationId, _project.Id);
        _workflow = new Workflow(statuses, transitions);
        _status = statuses.ToDictionary(s => s.Name);
    }

    private WorkItem NewItem(WorkItemType type = WorkItemType.Task, WorkItem? parent = null) =>
        new(_project, 1, type, "  Title  ", null, _status["Backlog"], Priority.Medium, Guid.NewGuid(), parent, Now);

    [Fact]
    public void New_item_is_trimmed_and_open()
    {
        var item = NewItem();
        Assert.Equal("Title", item.Title);
        Assert.Equal(StatusCategory.Todo, item.StatusCategory);
        Assert.False(item.IsClosed);
    }

    [Fact]
    public void Transition_must_follow_workflow()
    {
        var item = NewItem();
        var ex = Assert.Throws<DomainException>(() => item.TransitionTo(_workflow, _status["Hecho"].Id, 0, Now));
        Assert.Equal("workitem.invalid_transition", ex.Code);

        item.TransitionTo(_workflow, _status["En curso"].Id, 0, Now);
        item.TransitionTo(_workflow, _status["Hecho"].Id, 0, Now.AddHours(1));
        Assert.Equal(StatusCategory.Done, item.StatusCategory);
        Assert.Equal(Now.AddHours(1), item.CompletedAt);

        item.TransitionTo(_workflow, _status["En curso"].Id, 0, Now.AddHours(2));
        Assert.Null(item.CompletedAt);
    }

    [Fact]
    public void Cannot_complete_with_open_subtasks()
    {
        var item = NewItem();
        item.TransitionTo(_workflow, _status["En curso"].Id, 0, Now);
        var ex = Assert.Throws<DomainException>(() => item.TransitionTo(_workflow, _status["Hecho"].Id, 2, Now));
        Assert.Equal("workitem.open_subtasks", ex.Code);
        item.TransitionTo(_workflow, _status["Cancelado"].Id, 2, Now); // cancelling is allowed
    }

    [Fact]
    public void Subtask_rules()
    {
        Assert.Equal("workitem.subtask_requires_parent", Assert.Throws<DomainException>(() => NewItem(WorkItemType.Subtask)).Code);
        var parent = NewItem();
        Assert.Equal("workitem.parent_only_for_subtasks", Assert.Throws<DomainException>(() => NewItem(WorkItemType.Task, parent)).Code);
        var subtask = NewItem(WorkItemType.Subtask, parent);
        Assert.Equal(parent.Id, subtask.ParentId);
        Assert.Equal("workitem.nested_subtask", Assert.Throws<DomainException>(() => NewItem(WorkItemType.Subtask, subtask)).Code);
    }

    [Fact]
    public void Archived_project_is_read_only()
    {
        _project.Archive(Now);
        Assert.Equal("project.archived", Assert.Throws<DomainException>(() => NewItem()).Code);
    }

    [Fact]
    public void Labels_are_capped_and_replaced()
    {
        var item = NewItem();
        var labels = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        item.SetLabels(labels, Now);
        item.SetLabels([labels[0]], Now);
        Assert.Equal(labels[0], Assert.Single(item.Labels).LabelId);
        Assert.Throws<DomainException>(() => item.SetLabels(Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToList(), Now));
    }

    [Fact]
    public void Default_workflow_has_initial_todo_status()
    {
        Assert.Equal("Backlog", _workflow.InitialStatus.Name);
        Assert.True(_workflow.CanTransition(_status["Cancelado"].Id, _status["Backlog"].Id));
        Assert.False(_workflow.CanTransition(_status["Backlog"].Id, _status["Hecho"].Id));
    }
}

public class ApplicationHelpersTests
{
    [Theory]
    [InlineData("Acme Labs", "acme-labs")]
    [InlineData("  Ñandú & Co.  ", "nandu-co")]
    [InlineData("!!!", "org")]
    public void Slug_is_url_safe(string input, string expected) => Assert.Equal(expected, Slug.From(input));

    [Fact]
    public void Tokens_are_random_and_hash_is_stable()
    {
        var a = SecureTokens.Generate();
        var b = SecureTokens.Generate();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43);
        Assert.Equal(SecureTokens.Hash(a), SecureTokens.Hash(a));
        Assert.True(SecureTokens.FixedTimeEqualsHex(SecureTokens.Hash(a), SecureTokens.Hash(a)));
        Assert.False(SecureTokens.FixedTimeEqualsHex(SecureTokens.Hash(a), SecureTokens.Hash(b)));
    }

    [Fact]
    public void Audit_details_redact_secrets()
    {
        var sanitized = AuditLog.Sanitize(new() { ["webhookSecret"] = "s3cr3t", ["accessToken"] = "t", ["name"] = "Acme", ["long"] = new string('x', 400) });
        Assert.Equal("[redacted]", sanitized["webhookSecret"]);
        Assert.Equal("[redacted]", sanitized["accessToken"]);
        Assert.Equal("Acme", sanitized["name"]);
        Assert.True(sanitized["long"]!.Length <= 301);
    }

    [Theory]
    [InlineData(OrgRole.Viewer, ProjectRole.Member, false, false, false, false)]
    [InlineData(OrgRole.Developer, null, false, false, false, false)]
    [InlineData(OrgRole.Developer, ProjectRole.Member, false, true, false, false)]
    [InlineData(OrgRole.Developer, ProjectRole.Lead, false, true, true, false)]
    [InlineData(OrgRole.Admin, null, false, true, true, true)]
    [InlineData(OrgRole.Admin, null, true, false, true, true)]
    public void Project_permissions_matrix(OrgRole orgRole, ProjectRole? projectRole, bool archived, bool edit, bool manage, bool delete)
    {
        var p = ProjectPermissions.For(orgRole, projectRole, archived);
        Assert.Equal((edit, manage, delete), (p.CanEditItems, p.CanManage, p.CanDelete));
    }
}
