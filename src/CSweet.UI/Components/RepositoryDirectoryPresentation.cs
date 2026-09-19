using System.Text.RegularExpressions;
using CSweet.Contracts.SourceControl;

namespace CSweet.UI.Components;

public static partial class RepositoryDirectoryPresentation
{
    public static string RelativeTime(DateTimeOffset occurred, DateTimeOffset now)
    {
        var age = now - occurred;
        if (age.TotalMinutes < 1) return "Just now";
        if (age.TotalHours < 1) return Ago((int)age.TotalMinutes, "minute");
        if (age.TotalDays < 1) return Ago((int)age.TotalHours, "hour");
        if (age.TotalDays < 30) return Ago((int)age.TotalDays, "day");
        if (age.TotalDays < 365) return Ago((int)(age.TotalDays / 30), "month");
        return Ago((int)(age.TotalDays / 365), "year");
    }

    public static string Activity(SourceControlRepositorySummary repository)
    {
        var label = repository.LastEventType switch
        {
            "SourceControl.Repository.Create" => "Created repository",
            "SourceControl.Repository.Update" => "Updated repository",
            "SourceControl.Repository.Ref" => "Updated a branch or tag",
            "SourceControl.Repository.Team" => "Updated team access",
            "SourceControl.Git.PushStarted" => "Started a Git push",
            "SourceControl.Git.PushTransferCompleted" => "Transferred Git changes",
            "SourceControl.Git.CreateAccess" or "SourceControl.Git.AccessCreated" => "Created Git access",
            "SourceControl.Git.RevokeAccess" or "SourceControl.Git.AccessRevoked" => "Revoked Git access",
            "SourceControl.Workspace.Ready" => "Prepared a workspace",
            "SourceControl.Workspace.Preparing" => "Preparing a workspace",
            "SourceControl.Workspace.Pending" => "Queued a workspace",
            "SourceControl.Workspace.Published" => "Published workspace changes",
            "SourceControl.Workspace.Failed" => "Workspace preparation failed",
            "SourceControl.Workspace.Removed" => "Removed a workspace",
            "SourceControl.Publication.Merged" => "Merged changes",
            "SourceControl.Publication.Published" => "Published changes",
            "SourceControl.Publication.AwaitingValidation" => "Submitted changes for validation",
            "SourceControl.Publication.AwaitingLeadAuthorization" => "Requested team lead review",
            "SourceControl.Publication.AwaitingAdministratorApproval" => "Requested administrator approval",
            "SourceControl.Publication.ReadyToMerge" => "Changes ready to merge",
            "SourceControl.Publication.BranchPublishedExternalMerge" => "Published a branch for review",
            "SourceControl.Publication.Superseded" => "Superseded earlier changes",
            "SourceControl.Publication.Failed" => "Publication failed",
            _ => Humanize(repository.LastEventType ?? "Repository activity")
        };
        return repository.LastEventOutcome switch
        {
            "Failed" => $"{label} · Failed",
            "Started" => $"{label} · Started",
            _ => label
        };
    }

    private static string Ago(int value, string unit) => $"{value} {unit}{(value == 1 ? "" : "s")} ago";
    private static string Humanize(string value) => WordBoundary().Replace(
        value.Replace("SourceControl.", "", StringComparison.Ordinal).Replace('.', ' '), "$1 $2");
    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex WordBoundary();
}
