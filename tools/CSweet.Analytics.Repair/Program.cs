using CSweet.Infrastructure.Analytics;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// Read credentials through stdin; never place them in command arguments or diagnostic output.
var connection = Console.ReadLine() ?? throw new InvalidOperationException("Supply the PostgreSQL connection string on stdin.");
Guid? organization = args.Length > 0 && args[0] != "--dry-run" ? Guid.Parse(args[0]) : null;
await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection).Options);
await using var transaction = await db.Database.BeginTransactionAsync();
var repaired = await InferenceSourceAttribution.BackfillAsync(db, organization);
if (args.Contains("--dry-run")) await transaction.RollbackAsync();
else await transaction.CommitAsync();
Console.WriteLine($"{(args.Contains("--dry-run") ? "Would repair" : "Repaired")} {repaired} inference receipts. Token counts unchanged.");
