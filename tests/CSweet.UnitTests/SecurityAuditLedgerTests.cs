using System.Text;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class SecurityAuditLedgerTests
{
    [Fact]
    public void JsonEvidence_RedactsSecretsAndHashesExactRawBytes()
    {
        var raw = Encoding.UTF8.GetBytes("""{"user":"Ada","access_token":"secret-token","nested":{"Password":"do-not-store"}}""");

        var evidence = AuditPayloadSanitizer.Capture(raw, "application/json");

        Assert.Equal(raw.Length, evidence.Size);
        Assert.Contains("Ada", evidence.Preview);
        Assert.DoesNotContain("secret-token", evidence.Preview);
        Assert.DoesNotContain("do-not-store", evidence.Preview);
        Assert.Contains("[REDACTED]", evidence.Preview);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)), evidence.Sha256);
    }

    [Fact]
    public void BinaryEvidence_StoresHashAndSizeWithoutReadablePreview()
    {
        var raw = new byte[] { 0, 1, 2, 255 };
        var evidence = AuditPayloadSanitizer.Capture(raw, "application/octet-stream");
        Assert.Null(evidence.Preview);
        Assert.Equal(raw.Length, evidence.Size);
        Assert.False(evidence.Truncated);
    }

    [Fact]
    public void RecordHash_IsDeterministicAndCoversIdentity()
    {
        var item = NewEvent();
        var first = AuditIntegrity.ComputeRecordHash(item);
        var second = AuditIntegrity.ComputeRecordHash(item);
        item.ActorAgentId = "different-agent";
        var changed = AuditIntegrity.ComputeRecordHash(item);
        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void TimestampNormalization_UsesUtcPostgresMicrosecondPrecision()
    {
        var source = new DateTimeOffset(2026, 7, 26, 18, 30, 0, TimeSpan.FromHours(-7))
            .AddTicks(123_456_7);

        var normalized = AuditEventWriter.NormalizeTimestamp(source);

        Assert.Equal(TimeSpan.Zero, normalized.Offset);
        Assert.Equal(0, normalized.Ticks % 10);
        Assert.Equal(source.ToUniversalTime().Ticks / 10, normalized.Ticks / 10);
    }

    [Fact]
    public async Task DbContext_RejectsAuditUpdateAndDelete()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var item = NewEvent();
        db.AuditEvents.Add(item);
        await db.SaveChangesAsync();
        item.Outcome = "Changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.Entry(item).State = EntityState.Unchanged;
        db.AuditEvents.Remove(item);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    private static AuditEvent NewEvent() => new()
    {
        Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Category = "BrokerEvent",
        Direction = "Inbound", Outcome = "Received", EventType = "test.event",
        EntityType = "DeliveredEvent", OccurredAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow, TraceId = Guid.NewGuid(), ActorKind = "Agent",
        IdentityVerified = true, ActorAgentId = "agent", IntegrityVersion = 1
    };
}
