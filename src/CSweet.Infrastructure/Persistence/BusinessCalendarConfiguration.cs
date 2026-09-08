using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Persistence;

internal static class BusinessCalendarConfiguration
{
    public static void Apply(ModelBuilder model)
    {
        model.Entity<BusinessCalendar>(e =>
        {
            e.ToTable("BusinessCalendars"); e.HasKey(x => x.OrganizationId);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<BusinessCalendarEvent>(e =>
        {
            e.ToTable("BusinessCalendarEvents"); e.HasKey(x => x.Id);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.Property(x => x.CreationKey).HasMaxLength(200);
            e.HasIndex(x => new { x.OrganizationId, x.CreationKey }).IsUnique();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<BusinessCalendarException>(e =>
        {
            e.ToTable("BusinessCalendarExceptions"); e.HasKey(x => new { x.EventId, x.OccurrenceLocal });
            e.Property(x => x.OccurrenceLocal).HasColumnType("timestamp without time zone");
            e.HasOne<BusinessCalendarEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<BusinessCalendarDispatch>(e =>
        {
            e.ToTable("BusinessCalendarDispatches"); e.HasKey(x => x.Id);
            e.Property(x => x.OccurrenceLocal).HasColumnType("timestamp without time zone");
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.EventId, x.OccurrenceLocal, x.Kind, x.RecipientId }).IsUnique();
            e.HasIndex(x => new { x.Status, x.DueAt });
            e.HasOne<BusinessCalendarEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<BusinessCalendarReminder>(e =>
        {
            e.ToTable("BusinessCalendarReminders"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OrganizationId, x.RecipientId, x.Read });
            e.HasOne<BusinessCalendarEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<BusinessCalendarChange>(e =>
        {
            e.ToTable("BusinessCalendarChanges"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OrganizationId, x.Id });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
