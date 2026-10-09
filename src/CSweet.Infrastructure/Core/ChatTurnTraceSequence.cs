using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CSweet.Infrastructure.Core;

internal static class ChatTurnTraceSequence
{
    // Writers use independent, long-lived contexts. Allocate in the database rather than
    // incrementing a potentially stale tracked turn. Gaps after failed inserts are harmless.
    internal static async Task<long> NextAsync(CSweetDbContext db, ChatTurn turn, CancellationToken token)
    {
        if (!db.Database.IsRelational()) return turn.NextTraceSequence++;
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                UPDATE "ChatTurns" SET "NextTraceSequence" = "NextTraceSequence" + 1
                WHERE "Id" = @id RETURNING "NextTraceSequence" - 1
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = turn.Id;
            command.Parameters.Add(parameter);
            var value = await command.ExecuteScalarAsync(token);
            if (value is null || value is DBNull) throw new InvalidOperationException("The chat turn no longer exists.");
            var sequence = Convert.ToInt64(value);
            // Prevent a later SaveChanges in this context from overwriting another writer's counter.
            var property = db.Entry(turn).Property(x => x.NextTraceSequence);
            property.CurrentValue = property.OriginalValue = sequence + 1;
            property.IsModified = false;
            return sequence;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
