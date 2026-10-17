# Repair inference attribution

Build with `dotnet build tools/CSweet.Analytics.Repair -c Release`. Run the resulting
DLL with an optional organization UUID and `--dry-run`. Supply the PostgreSQL
connection string as one line on stdin, keeping credentials out of command arguments
and logs. Dry-run performs the same repair inside a rolled-back transaction.

The command uses `InferenceSourceAttribution.BackfillAsync`, also called by the database
initializer on deployment. It fills missing ownership on completed, non-erased provider
receipts only. It never changes token amounts, moves already captured ownership, divides
conversation usage among tasks, or interprets prompts. Repeating it is safe; active streams
are skipped and can be repaired after completion. Unknown ownership remains unknown.
