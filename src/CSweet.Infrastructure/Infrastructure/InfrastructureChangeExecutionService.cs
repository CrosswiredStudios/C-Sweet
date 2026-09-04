using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Core;
using CSweet.Application.Infrastructure;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Infrastructure;

public sealed class InfrastructureChangeExecutionService(
    CSweetDbContext db,
    IInfrastructureProviderGateway gateway,
    IAuditEventWriter audit) : IInfrastructureChangeExecutionService, IManagedActionExecutor
{
    public const string ActionType = "infrastructure.change";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool CanExecute(string actionType) => actionType == ActionType;

    public async Task<ManagedActionExecutionResult> ExecuteAsync(ActionProposal proposal,
        OrganizationUser approvingActor, CancellationToken cancellationToken = default)
    {
        var receipts = await ExecuteAsync(proposal, cancellationToken);
        return new ManagedActionExecutionResult(proposal.Id, receipts.Count,
            $"Executed {receipts.Count} approved infrastructure operation(s).");
    }

    public async Task<IReadOnlyList<InfrastructureOperationReceipt>> ExecuteAsync(ActionProposal proposal,
        CancellationToken cancellationToken = default)
    {
        if (proposal.ActionType != ActionType)
            throw new InvalidOperationException("The proposal is not an infrastructure change.");
        var envelope = JsonSerializer.Deserialize<InfrastructureChangeEnvelope>(proposal.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("The infrastructure proposal payload is invalid.");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(envelope.Change, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
        if (!FixedEquals(hash, envelope.PayloadHash))
            throw new InvalidOperationException("The approved infrastructure payload hash does not match its operations.");

        var receipts = new List<InfrastructureOperationReceipt>();
        foreach (var operation in envelope.Change.Operations)
        {
            var externalKey = $"{proposal.Id:N}:{Hash(operation.IdempotencyKey)}";
            var record = await db.PluginOperationalStates.SingleOrDefaultAsync(x =>
                x.OrganizationId == proposal.OrganizationId && x.AgentInstallationId == proposal.AgentInstallationId &&
                x.Kind == "infrastructure-operation-receipt" && x.ExternalKey == externalKey, cancellationToken);
            if (record is not null)
            {
                var stored = JsonSerializer.Deserialize<InfrastructureOperationReceipt>(record.PayloadJson, JsonOptions)
                    ?? throw new InvalidOperationException("The existing infrastructure receipt is invalid.");
                if (stored.Status == "Succeeded") { receipts.Add(stored); continue; }
                throw new InvalidOperationException(
                    "A previous provider attempt has an ambiguous result. Reconcile provider state before retrying.");
            }

            var started = DateTimeOffset.UtcNow;
            record = new PluginOperationalState
            {
                Id = Guid.NewGuid(), OrganizationId = proposal.OrganizationId,
                AgentInstallationId = proposal.AgentInstallationId, Kind = "infrastructure-operation-receipt",
                ExternalKey = externalKey, Revision = 1, CreatedAt = started, UpdatedAt = started,
                PayloadJson = JsonSerializer.Serialize(new InfrastructureOperationReceipt(
                    Guid.NewGuid(), proposal.Id, operation.Capability, "Started",
                    JsonSerializer.SerializeToElement(new { }), null, null, externalKey, started, started), JsonOptions)
            };
            db.PluginOperationalStates.Add(record);
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                JsonElement result;
                if (operation.Capability == InfrastructureCapabilityNames.FileTransfer)
                {
                    var transfer = operation.Input.Deserialize<InfrastructureFileTransferRequest>(JsonOptions)
                        ?? throw new InvalidOperationException("The approved file-transfer input is invalid.");
                    result = JsonSerializer.SerializeToElement(await gateway.TransferAsync(
                        proposal.OrganizationId, proposal.AgentInstallationId, transfer, cancellationToken), JsonOptions);
                }
                else
                {
                    result = await gateway.InvokeApprovedAsync(proposal.OrganizationId,
                        proposal.AgentInstallationId, operation.Capability, operation.Input, cancellationToken);
                }
                var completed = DateTimeOffset.UtcNow;
                var receipt = new InfrastructureOperationReceipt(record.Id, proposal.Id, operation.Capability,
                    "Succeeded", result, null, Hash(result.GetRawText()), externalKey, started, completed);
                record.PayloadJson = JsonSerializer.Serialize(receipt, JsonOptions);
                record.Revision++;
                record.UpdatedAt = completed;
                await db.SaveChangesAsync(cancellationToken);
                await audit.WriteAsync("infrastructure.operation.succeeded", nameof(ActionProposal), proposal.Id,
                    $"Executed approved infrastructure capability {operation.Capability}.",
                    JsonSerializer.Serialize(new { proposal.OrganizationId, proposal.AgentInstallationId,
                        proposalId = proposal.Id, operation.Capability, receipt.CorrelationId }), cancellationToken);
                receipts.Add(receipt);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failed = new InfrastructureOperationReceipt(record.Id, proposal.Id, operation.Capability,
                    "ReconciliationRequired", JsonSerializer.SerializeToElement(new
                    {
                        error = "provider_result_ambiguous",
                        message = "The provider result must be reconciled before retry."
                    }), null, null, externalKey, started, DateTimeOffset.UtcNow);
                record.PayloadJson = JsonSerializer.Serialize(failed, JsonOptions);
                record.Revision++;
                record.UpdatedAt = failed.CompletedAt;
                await db.SaveChangesAsync(cancellationToken);
                await audit.WriteAsync("infrastructure.operation.reconciliation-required", nameof(ActionProposal), proposal.Id,
                    "An approved infrastructure operation requires state reconciliation.",
                    JsonSerializer.Serialize(new { proposal.OrganizationId, proposal.AgentInstallationId,
                        proposalId = proposal.Id, operation.Capability, errorType = exception.GetType().Name }), cancellationToken);
                throw new InvalidOperationException("The provider result is ambiguous. Reconcile state before retrying.");
            }
        }
        return receipts;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.ASCII.GetBytes(left.ToLowerInvariant());
        var b = Encoding.ASCII.GetBytes(right.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
