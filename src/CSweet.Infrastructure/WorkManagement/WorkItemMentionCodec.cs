using System.Text.Json;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public static class WorkItemMentionCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record NormalizedWorkItemText(
        string Title,
        string Description,
        IReadOnlyList<Wire.WorkItemMentionSpan> Mentions)
    {
        public string MentionsJson => JsonSerializer.Serialize(Mentions, JsonOptions);
    }

    public static async Task<NormalizedWorkItemText> NormalizeAndValidateAsync(
        CSweetDbContext db,
        Guid organizationId,
        string title,
        string? description,
        IReadOnlyList<Wire.WorkItemMentionInput>? mentions,
        CancellationToken cancellationToken)
    {
        var rawTitle = title ?? string.Empty;
        var rawDescription = description ?? string.Empty;
        var normalizedTitle = rawTitle.Trim();
        var normalizedDescription = rawDescription.Trim();
        if (mentions is null || mentions.Count == 0)
            return new(normalizedTitle, normalizedDescription, []);
        if (mentions.Count > 100)
            throw new ArgumentException("A work item cannot contain more than 100 mention spans.");

        var duplicate = mentions.GroupBy(x => new
        {
            x.OrganizationUserId,
            Field = x.Field?.Trim(),
            x.Offset,
            x.Length
        }).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException("A work-item mention span is duplicated.");

        var mentionedIds = mentions.Select(x => x.OrganizationUserId).Distinct().ToList();
        var people = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive && mentionedIds.Contains(x.Id))
            .Select(x => new { x.Id, x.DisplayName, x.EmployeeType })
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        if (people.Count != mentionedIds.Count)
            throw new ArgumentException("Every mentioned identity must be active in this organization.");

        var result = new List<Wire.WorkItemMentionSpan>(mentions.Count);
        foreach (var mention in mentions)
        {
            var field = mention.Field?.Trim() ?? string.Empty;
            if (!Wire.WorkItemMentionFields.All.Contains(field))
                throw new ArgumentException("A work-item mention must target the title or description field.");
            if (mention.Offset < 0 || mention.Length <= 0)
                throw new ArgumentException("A work-item mention span is out of range.");

            var raw = field == Wire.WorkItemMentionFields.Title ? rawTitle : rawDescription;
            var normalized = field == Wire.WorkItemMentionFields.Title
                ? normalizedTitle
                : normalizedDescription;
            var leadingTrim = raw.Length - raw.TrimStart().Length;
            var offset = mention.Offset - leadingTrim;
            var person = people[mention.OrganizationUserId];
            var displayText = $"@{person.DisplayName}";
            if (offset < 0 || offset + mention.Length > normalized.Length ||
                mention.Length != displayText.Length ||
                !normalized.AsSpan(offset, mention.Length).SequenceEqual(displayText))
                throw new ArgumentException(
                    $"The visible mention for {person.DisplayName} does not match its structured identity.");

            result.Add(new Wire.WorkItemMentionSpan(
                person.Id,
                person.DisplayName,
                person.EmployeeType.ToString(),
                field,
                offset,
                mention.Length,
                displayText));
        }

        foreach (var fieldMentions in result.GroupBy(x => x.Field))
        {
            var ordered = fieldMentions.OrderBy(x => x.Offset).ToList();
            for (var index = 1; index < ordered.Count; index++)
                if (ordered[index - 1].Offset + ordered[index - 1].Length > ordered[index].Offset)
                    throw new ArgumentException("Work-item mention spans cannot overlap.");
        }

        return new(normalizedTitle, normalizedDescription,
            result.OrderBy(x => x.Field).ThenBy(x => x.Offset).ToList());
    }

    public static IReadOnlyList<Wire.WorkItemMentionSpan> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<Wire.WorkItemMentionSpan>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
