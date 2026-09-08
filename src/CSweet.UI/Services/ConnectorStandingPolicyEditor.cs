using System.Text.Json;
using CSweet.Contracts.Plugins;

namespace CSweet.UI.Services;

/// <summary>Local native form state. It never grants authority or executes a connector operation.</summary>
public sealed class ConnectorStandingPolicyEditor
{
    private readonly ConnectorStandingPolicySetup setup;
    private readonly DateTimeOffset createdAt;
    public List<FieldChoice> Fields { get; }
    public IReadOnlyCollection<int> Days { get; set; }
    public string TimeZoneId { get; set; }
    public bool AllDay { get; set; }
    public TimeSpan? StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }
    public bool EndAtMidnight { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public TimeSpan? ExpiryTime { get; set; }
    public int MaximumActionsPerHour { get; set; }
    public string EscalationTerms { get; set; }
    public string ScheduledFieldKey { get; set; }
    public decimal MaximumMediaMegabytes { get; set; }
    public IReadOnlyCollection<string> AllowedMediaTypes { get; set; }

    public ConnectorStandingPolicyEditor(ConnectorStandingPolicySetup setup, DateTimeOffset? now = null)
    {
        this.setup = setup; createdAt = now ?? DateTimeOffset.UtcNow;
        var review = setup.Review ?? throw new ArgumentException("A current review is required.");
        var policy = setup.Policy?.Definition;
        Fields = review.FieldReviews.Select(field => new FieldChoice(field, policy?.Fields.SingleOrDefault(x => x.Field == field.Field))).ToList();
        Days = policy?.DaysOfWeek ?? [0, 1, 2, 3, 4, 5, 6];
        TimeZoneId = policy?.TimeZoneId ?? TimeZoneInfo.Local.Id;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        AllDay = policy is null || policy.StartMinute == 0 && policy.EndMinute == 1440;
        StartTime = TimeSpan.FromMinutes(policy?.StartMinute ?? 540);
        EndAtMidnight = policy?.EndMinute == 1440;
        EndTime = TimeSpan.FromMinutes(policy?.EndMinute is { } end && end < 1440 ? end : 1020);
        var expiry = TimeZoneInfo.ConvertTime(policy?.ExpiresAt ?? createdAt.AddDays(14), zone);
        ExpiryDate = expiry.Date; ExpiryTime = expiry.TimeOfDay;
        MaximumActionsPerHour = policy?.MaximumActionsPerHour ?? 5;
        EscalationTerms = string.Join(Environment.NewLine, policy?.EscalationTerms ?? []);
        ScheduledFieldKey = policy?.ScheduledAt is { } scheduled ? Key(scheduled) : "";
        MaximumMediaMegabytes = (policy?.MaximumMediaBytes ?? review.Media?.SizeBytes ?? 1048576L) / 1048576m;
        AllowedMediaTypes = policy?.AllowedMediaTypes ?? (review.Media is { } media ? [media.ContentType] : []);
    }

    public ApproveConnectorStandingPolicyRequest Build()
    {
        var review = setup.Review!;
        if (!review.CanUseStandingPolicy || Fields.Count != review.MutableFields.Count || MaximumActionsPerHour is < 1 or > 1000 ||
            !Days.Any() || ExpiryDate is null || ExpiryTime is null || ExpiryTime < TimeSpan.Zero || ExpiryTime >= TimeSpan.FromDays(1))
            throw new ArgumentException("Choose complete field rules, a valid expiry and an hourly action limit.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        var localExpiry = DateTime.SpecifyKind(ExpiryDate.Value.Date + ExpiryTime.Value, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(localExpiry) || zone.IsAmbiguousTime(localExpiry))
            throw new ArgumentException("That expiry falls in a daylight-saving clock change. Choose another time.");
        var expiry = new DateTimeOffset(localExpiry, zone.GetUtcOffset(localExpiry));
        if (expiry <= createdAt || expiry > createdAt.AddDays(366)) throw new ArgumentException("Choose an expiry within the next year.");
        var start = AllDay ? 0 : Minutes(StartTime);
        var end = AllDay || EndAtMidnight ? 1440 : Minutes(EndTime);
        if (start == end) throw new ArgumentException("Choose different start and end times, or allow the whole day.");
        var scheduled = ScheduledFieldKey.Length == 0 ? null : Fields.Single(x => Key(x.Review.Field) == ScheduledFieldKey).Review.Field;
        if (review.Media is not null && (MaximumMediaMegabytes * 1048576m < 1 || MaximumMediaMegabytes > 262144 || !AllowedMediaTypes.Any()))
            throw new ArgumentException("Choose at least one file type and a size limit no larger than 256 GB.");
        var definition = new ConnectorStandingPolicyDefinition(Fields.Select(x => x.Build()).ToArray(), Days.Distinct().Order().ToArray(),
            start, end, MaximumActionsPerHour, setup.Policy?.Definition.NotBefore ?? createdAt, expiry,
            EscalationTerms.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), scheduled,
            review.Media is null ? null : checked((long)decimal.Floor(MaximumMediaMegabytes * 1048576m)), review.Media is null ? null : AllowedMediaTypes.ToArray(), TimeZoneId);
        return new(review.TemplatePlanId, review.PlanHash, review.ReviewHash, definition, setup.Policy?.Revision);
    }

    private static int Minutes(TimeSpan? time) => time is { } value && value >= TimeSpan.Zero && value < TimeSpan.FromDays(1)
        ? (int)value.TotalMinutes : throw new ArgumentException("Choose a valid daily start and end time.");
    public static string Key(ConnectorPolicyField field) => field.Source + ":" + field.Path;
    public static string Label(JsonElement? value) => value is null ? "Not set" : value.Value.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString()!, JsonValueKind.True => "Yes", JsonValueKind.False => "No",
        JsonValueKind.Array => string.Join(", ", value.Value.EnumerateArray().Select(x => Label(x))),
        JsonValueKind.Object => string.Join("; ", value.Value.EnumerateObject().Select(x => x.Name + ": " + Label(x.Value))),
        JsonValueKind.Null => "Not set", _ => value.Value.ToString()
    };
    public sealed class FieldChoice
    {
        private readonly ConnectorPolicyFieldRule? existing;
        public ConnectorPolicyFieldReview Review { get; }
        public string Mode { get; set; }
        public bool AllowOmission { get; set; }
        public IReadOnlyCollection<string> SelectedValues { get; set; }
        public string ExistingDescription => existing is null ? "" : string.Join("; ", existing.AllowedValues.Select(x => Label(x)));
        public bool HasExisting => existing is not null;
        public FieldChoice(ConnectorPolicyFieldReview review, ConnectorPolicyFieldRule? existing)
        {
            Review = review; this.existing = existing;
            Mode = existing is null ? "Reviewed" : existing.AllowAny ? "Any" : "Saved";
            AllowOmission = existing?.AllowOmission ?? review.CurrentValue is null;
            SelectedValues = existing?.AllowedValues.Select(x => x.GetRawText()).ToArray() ??
                (review.CurrentValue is { } value ? [value.GetRawText()] : []);
        }
        public ConnectorPolicyFieldRule Build()
        {
            var values = Mode switch
            {
                "Any" => [], "Saved" when existing is not null => existing.AllowedValues.ToArray(),
                "Reviewed" => Review.CurrentValue is { } value ? new[] { value.Clone() } : [],
                "Choices" => Review.SuggestedValues.Where(x => SelectedValues.Contains(x.GetRawText(), StringComparer.Ordinal)).ToArray(),
                _ => throw new ArgumentException("Choose a rule for every field.")
            };
            if (Mode != "Any" && values.Length == 0 && !AllowOmission)
                throw new ArgumentException($"Choose permitted values for {Review.Label}, or explicitly allow it to be unset.");
            return new(Review.Field, Mode == "Any", values, AllowOmission);
        }
    }
}
