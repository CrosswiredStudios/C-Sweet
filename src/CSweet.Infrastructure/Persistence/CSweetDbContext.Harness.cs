using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

public sealed partial class CSweetDbContext
{
    public DbSet<AgentHarnessPolicy> AgentHarnessPolicies => Set<AgentHarnessPolicy>();
    public DbSet<BusinessSkillRevision> BusinessSkillRevisions => Set<BusinessSkillRevision>();
    public DbSet<HarnessTaskBudget> HarnessTaskBudgets => Set<HarnessTaskBudget>();
    public DbSet<HarnessBudgetReservation> HarnessBudgetReservations => Set<HarnessBudgetReservation>();

    private static void ConfigureHarness(ModelBuilder model)
    {
        model.Entity<AgentHarnessPolicy>(e => {
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne<AgentInstallation>().WithMany().HasForeignKey(x => x.Id).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<BusinessSkillRevision>(e => {
            e.Property(x => x.BusinessId).HasMaxLength(80);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Digest).HasMaxLength(64);
            e.HasIndex(x => new { x.BusinessId, x.Name, x.Version }).IsUnique();
        });
        model.Entity<HarnessTaskBudget>(e => {
            e.Property(x => x.Id).HasMaxLength(160);
            e.Property(x => x.ChargedCost).HasPrecision(24, 10);
            e.Property(x => x.CostLimit).HasPrecision(24, 10);
        });
        model.Entity<HarnessBudgetReservation>(e => {
            e.Property(x => x.BudgetId).HasMaxLength(160);
            e.Property(x => x.Cost).HasPrecision(24, 10);
            e.HasIndex(x => x.RunId);
            e.HasOne<HarnessTaskBudget>().WithMany().HasForeignKey(x => x.BudgetId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
