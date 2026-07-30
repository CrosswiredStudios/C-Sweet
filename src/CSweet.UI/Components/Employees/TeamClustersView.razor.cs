namespace CSweet.UI.Components.Employees;

public enum TeamUiActionKind
{
    Create,
    Edit,
    Archive,
    Restore,
    AddMember,
    RemoveMember
}

public sealed record TeamUiActionRequest(
    TeamUiActionKind Action,
    Guid? TeamId = null,
    Guid? OrganizationUserId = null);
