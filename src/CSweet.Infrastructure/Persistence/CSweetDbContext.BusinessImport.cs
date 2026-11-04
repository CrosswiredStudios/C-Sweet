using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    public DbSet<BusinessSuggestion> BusinessSuggestions => Set<BusinessSuggestion>();
    public DbSet<ProjectRepositoryAssociation> ProjectRepositoryAssociations => Set<ProjectRepositoryAssociation>();
    public DbSet<ImportedProjectProfile> ImportedProjectProfiles => Set<ImportedProjectProfile>();

    private static void ConfigureBusinessImport(ModelBuilder model)
    {
        model.Entity<BusinessSuggestion>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.OrganizationId, x.Key }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(160); e.Property(x => x.Kind).HasMaxLength(64); e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Revision).IsConcurrencyToken(); e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade); });
        model.Entity<ProjectRepositoryAssociation>(e => { e.HasKey(x => x.ProjectId); e.HasIndex(x => new { x.OrganizationId, x.RepositoryId }).IsUnique();
            e.HasIndex(x => x.ImportSourceId).IsUnique(); e.HasOne<Workstream>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CSweet.Domain.Setup.SourceControlRepository>().WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<ImportedProjectProfile>(e => { e.HasKey(x => x.ProjectId); e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Revision).IsConcurrencyToken(); e.Property(x => x.Error).HasMaxLength(2048);
            e.HasOne<Workstream>().WithOne().HasForeignKey<ImportedProjectProfile>(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade); });
        model.Entity<ImportedProjectProfile>().HasIndex(x => new { x.AnalysisDraftId, x.Status });
        model.Entity<CSweet.Domain.PersonalAssistant.RepositoryOnboardingDraft>().HasIndex(x => new { x.OrganizationId, x.UpdatedAt });
        model.Entity<BusinessOnboardingOperation>().Property(x => x.StartMode).HasMaxLength(20).HasDefaultValue("New");
    }

    private async Task CaptureBusinessSuggestionsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var orgs = ChangeTracker.Entries<LeadershipAssignment>().Where(x => (x.State is EntityState.Added or EntityState.Modified) &&
            x.Entity.PositionKey == "chief-of-staff" && x.Entity.EndsAt == null && x.Entity.StartsAt <= now)
            .Select(x => x.Entity.OrganizationId).Distinct().ToArray();
        foreach (var org in orgs)
        {
            var leaders = ChangeTracker.Entries<LeadershipAssignment>().Where(x => x.Entity.OrganizationId == org &&
                x.Entity.PositionKey == "chief-of-staff" && x.Entity.EndsAt == null).Select(x => x.Entity.OrganizationUserId).ToArray();
            if (!CoreOrganizationUsers.Local.Any(x => leaders.Contains(x.Id) && x.IsActive && x.ArchivedAt == null) &&
                !await CoreOrganizationUsers.AnyAsync(x => leaders.Contains(x.Id) && x.IsActive && x.ArchivedAt == null, ct)) continue;
            var pending = await BusinessSuggestions.Where(x => x.OrganizationId == org && x.Status == "Pending" && x.Kind == "HireChiefOfStaff").ToListAsync(ct);
            foreach (var item in pending.Concat(BusinessSuggestions.Local.Where(x => x.OrganizationId == org && x.Status == "Pending" && x.Kind == "HireChiefOfStaff")).Distinct())
            { item.Status = "Completed"; item.UpdatedAt = now; item.Revision++; }
        }
        foreach (var entry in ChangeTracker.Entries<BusinessSuggestion>().Where(x => x.State is EntityState.Added or EntityState.Modified).ToArray())
            BusinessSuggestionService.Queue(this, entry.Entity.OrganizationId, AppRealtimeEvents.SuggestionsChanged, entry.Entity.Id);
    }
}
