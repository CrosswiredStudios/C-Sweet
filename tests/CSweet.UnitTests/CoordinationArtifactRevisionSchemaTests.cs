using CSweet.Domain.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class CoordinationArtifactRevisionSchemaTests
{
    [Fact]
    public void ArtifactPagesCanBeRevisedAcrossTurnsWhileDeliveryIdentityStaysUnique()
    {
        using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);
        var entity = db.Model.FindEntityType(typeof(AgentCoordinationTurn))!;
        var pageIndex = entity.GetIndexes().Single(x => x.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { "SessionId", "ArtifactType", "ArtifactKey", "ArtifactPageOrdinal" }));
        Assert.False(pageIndex.IsUnique);
        foreach (var key in new[] { "Ordinal", "IdempotencyKey" })
            Assert.True(entity.GetIndexes().Single(x => x.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { "SessionId", key })).IsUnique);
    }
}
