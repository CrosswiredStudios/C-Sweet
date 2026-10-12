using System.Text.RegularExpressions;

namespace CSweet.Contracts.Security;

/// <summary>Short action labels for audit history, separate from tool instructions and wire keys.</summary>
public static class AuditActionDescriptions
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["read_resource_changes"] = "Read team change requests",
        ["propose_resource_change"] = "Propose a team change",
        ["decide_resource_change"] = "Review a team change request",
        ["read_staffing_replenishments"] = "Read replacement hiring requests",
        ["propose_staffing_replenishment"] = "Propose replacement hiring",
        ["decide_staffing_replenishment"] = "Review replacement hiring",
        ["get_available_agents"] = "Browse available agents",
        ["read_organization_snapshot"] = "Read company overview",
        ["read_agent_operating_state"] = "Read operating assessment",
        ["write_agent_operating_state"] = "Save operating assessment",
        ["read_management_portfolio"] = "Read managed projects",
        ["read_project_assignment"] = "Read project assignment",
        ["read_scoped_team_roster"] = "Read project team",
        ["read_workstream"] = "Read project details",
        ["propose_workstream_plan"] = "Propose a project plan",
        ["propose_profiled_workstream"] = "Propose a project",
        ["propose_workstream_change"] = "Propose a project change",
        ["read_workstream_gates"] = "Read project review gates",
        ["submit_workstream_gate"] = "Submit a project review gate",
        ["decide_workstream_gate"] = "Review a project gate",
        ["request_workstream_decision"] = "Request a project decision",
        ["read_workstream_decisions"] = "Read project decisions",
        ["decide_workstream_decision"] = "Make a project decision",
        ["read_eligible_toolchains"] = "Browse available build tools",
        ["request_delivery_build"] = "Request a build",
        ["read_delivery_builds"] = "Read build results",
        ["ask_user"] = "Ask the user a question",
        ["suggest_user_action"] = "Suggest an action to the user",
        ["upsert_hiring_recommendation"] = "Save a hiring recommendation",
        ["resolve_hiring_recommendation"] = "Resolve a hiring recommendation",
        ["withdraw_hiring_recommendation"] = "Withdraw a hiring recommendation",
        ["stage_hiring_workflow"] = "Prepare a hiring request",
        ["submit_delegated_hire"] = "Hire under an approved delegation",
        ["send_communication_message"] = "Send a chat message",
        ["create_communication_chat"] = "Create a chat",
        ["read_communication_chat"] = "Read chats",
        ["modify_communication_chat"] = "Update a chat",
        ["archive_communication_chat"] = "Archive a chat",
        ["start_agent_coordination"] = "Start agent collaboration",
        ["respond_to_agent_coordination"] = "Respond to agent collaboration",
        ["read_agent_coordination"] = "Read agent collaboration",
        ["list_agent_coordination"] = "List agent collaborations",
        ["resume_agent_coordination"] = "Resume agent collaboration",
        ["cancel_agent_coordination"] = "Cancel agent collaboration",
        ["start_work_item_coordination"] = "Request help with a work assignment",
        ["start_board_coordination"] = "Start board collaboration",
        ["request_connector_action"] = "Request a connected service action",
        ["read_connector_action"] = "Read a connected service action",
        ["cancel_connector_action"] = "Cancel a connected service action",
        ["preflight_work_sprint"] = "Check sprint readiness",
        ["set_work_item_sprint"] = "Assign work to a sprint",
        ["carry_over_work_sprint"] = "Carry unfinished work into another sprint",
        ["retry_blocked_work_stage"] = "Retry blocked work",
        ["create_artifact"] = "Create a document",
        ["get_artifact"] = "Read documents",
        ["create_artifact_revision"] = "Create a document revision",
        ["submit_artifact_revision"] = "Submit a document for review",
        ["decide_artifact_revision"] = "Review a document revision",
        ["decide_artifact_revision_structured"] = "Review a document revision",
        ["request_artifact_access"] = "Request document access",
        ["create_artifact_package"] = "Create a document package",
        ["get_artifact_package"] = "Read a document package",
        ["submit_artifact_package"] = "Submit a document package",
        ["decide_artifact_package"] = "Review a document package",
        ["list_personal_todos"] = "Read personal work queue",
        ["add_personal_todo"] = "Add personal work",
        ["reorder_personal_todo"] = "Reprioritize personal work",
        ["requeue_personal_todo"] = "Requeue personal work",
        ["activate_personal_todo"] = "Activate personal work",
        ["update_personal_todo"] = "Update personal work",
        ["archive_personal_todo"] = "Archive personal work",
        ["restore_personal_todo"] = "Restore personal work",
        ["claim_personal_todo"] = "Claim personal work",
        ["complete_personal_todo"] = "Complete personal work",
        ["block_personal_todo"] = "Report blocked personal work",
        ["release_personal_todo"] = "Release personal work",
        ["defer_personal_todo"] = "Defer personal work"
    };

    public static string ForTool(string name) => Labels.TryGetValue(name, out var label) ? label : Humanize(name);

    public static string Humanize(string key)
    {
        var words = Regex.Replace(key, @"(?<=[a-z0-9])(?=[A-Z])", " ")
            .Replace('_', ' ').Replace('-', ' ').Replace('.', ' ').Trim();
        return words.Length == 0 ? "Recorded activity" : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
