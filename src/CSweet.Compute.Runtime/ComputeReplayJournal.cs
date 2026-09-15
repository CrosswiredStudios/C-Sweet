using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Compute.Runtime;

public enum ComputeJournalDecision { Execute, Observe }

/// <summary>
/// Extracted from the PoC's durable-state mechanism. A provider must hold this journal lock
/// across its awaited effect, including termination of any helper process. Uncertain effects
/// are never blindly repeated. The installer initializes history; runtime reads never reset it.
/// </summary>
public sealed partial class ComputeReplayJournal
{
    private readonly string directory;
    private readonly ComputeProviderEnrollment enrollment;
    private readonly TimeProvider clock;
    private readonly ComputeProviderCapacity capacity;
    private readonly Action<string> verifyProtectedPath;
    private const int MaximumBytes = 128 * 1024 * 1024;

    public ComputeReplayJournal(string directory, ComputeProviderEnrollment enrollment, ComputeProviderCapacity capacity, TimeProvider clock)
        : this(directory, enrollment, capacity, clock, WindowsComputeProtectedPaths.Verify) { }

    // Tests exercise transaction mechanics in ordinary temporary directories; production uses
    // the public constructor's owner/ACL/link checks. Other platforms need their own protection.
    internal ComputeReplayJournal(string directory, ComputeProviderEnrollment enrollment, ComputeProviderCapacity capacity, TimeProvider clock, Action<string> protection)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An absolute protected journal directory is required.");
        if (!capacity.IsValid) throw new ArgumentException("Valid allocatable provider capacity is required.");
        this.directory = Path.GetFullPath(directory); this.enrollment = enrollment; this.capacity = capacity; this.clock = clock;
        verifyProtectedPath = protection;
        verifyProtectedPath(this.directory);
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        await using var held = await LockAsync(token);
        if (File.Exists(StatePath)) { await ReadAsync(token); return; }
        // A missing state file in a previously initialized directory is not a fresh install.
        if (Directory.EnumerateFileSystemEntries(directory).Any(x => Path.GetFileName(x) != "journal.lock"))
            throw new InvalidDataException("Compute history is missing; initialization cannot discard prior state.");
        await WriteAsync(new JournalState { NodeId = enrollment.NodeId, OrganizationId = enrollment.OrganizationId, ProviderId = enrollment.ProviderId }, token);
        var marker = Path.Combine(directory, "initialized");
        await using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(new byte[] { 1 }, token); stream.Flush(true);
    }

    public Task<T> RunAsync<T>(VerifiedComputeDispatch dispatch,
        Func<ComputeJournalDecision, CancellationToken, Task<T>> effect, CancellationToken token) =>
        RunPhysicalAsync(dispatch, async (decision, resourceId, cancellation) =>
            new ComputePhysicalOutcome<T>(await effect(decision, cancellation), resourceId), token);

    /// <summary>The backend receives the protected resource identity; newly discovered identity is persisted before returning.</summary>
    public async Task<T> RunPhysicalAsync<T>(VerifiedComputeDispatch dispatch,
        Func<ComputeJournalDecision, string?, CancellationToken, Task<ComputePhysicalOutcome<T>>> effect, CancellationToken token)
    {
        await using var held = await LockAsync(token);
        var claim = dispatch.Authorization;
        if (claim.ExpiresAt <= clock.GetUtcNow() || claim.NodeId != enrollment.NodeId || claim.OrganizationId != enrollment.OrganizationId ||
            claim.ProviderId != enrollment.ProviderId) throw new UnauthorizedAccessException("The dispatch expired or belongs to another provider.");
        var state = await ReadAsync(token);
        if (!state.Environments.TryGetValue(claim.EnvironmentId, out var environment))
        {
            state.Environments.Add(claim.EnvironmentId, environment = new()
            {
                InstallationId = claim.InstallationId, SpecificationDigest = claim.SpecificationDigest
            });
        }
        if (environment.InstallationId != claim.InstallationId || environment.SpecificationDigest != claim.SpecificationDigest ||
            claim.Generation < environment.Generation ||
            environment.ResourceId is not null && claim.ResourceId is not null && environment.ResourceId != claim.ResourceId ||
            claim.Generation == environment.Generation && environment.CurrentOperationId != claim.OperationId ||
            environment.DestroyRequested && claim.Action != InfrastructureActions.Destroy && claim.Mode == ComputeDispatchMode.Execute ||
            environment.Reservation is { } lease && (lease.LeaseExpired || lease.LeaseExpiresAt <= clock.GetUtcNow()) &&
            claim.Mode == ComputeDispatchMode.Execute && claim.Action is InfrastructureActions.Provision or InfrastructureActions.Start or InfrastructureActions.Restart or InfrastructureActions.Execute or InfrastructureActions.PublishPort)
            throw new UnauthorizedAccessException("Compute history fences this dispatch.");
        environment.Generation = claim.Generation; environment.CurrentOperationId = claim.OperationId;
        if (claim.Action == InfrastructureActions.Destroy) environment.DestroyRequested = true;
        if (!environment.Operations.TryGetValue(claim.OperationId, out var operation))
            environment.Operations.Add(claim.OperationId, operation = new() { Generation = claim.Generation, Action = claim.Action });
        if (operation.Generation != claim.Generation || operation.Action != claim.Action)
            throw new UnauthorizedAccessException("Compute operation identity has conflicting history.");
        var decision = ComputeJournalDecision.Observe;
        if (claim.Mode == ComputeDispatchMode.Execute && !operation.ExecutionClaimed)
        {
            if (claim.Action == InfrastructureActions.Provision)
            {
                if (environment.Reservation is not null)
                    throw new UnauthorizedAccessException("A physical reservation already exists; reconcile it before recreation.");
                Reserve(state, environment, dispatch);
            }
            else if (claim.Action is InfrastructureActions.Start or InfrastructureActions.Restart && (environment.Reservation is null || environment.ResourceId is null))
                throw new UnauthorizedAccessException("Activation requires protected capacity and resource identity history.");
            operation.ExecutionClaimed = true; operation.TemplateDigest = claim.TemplateDigest;
            decision = ComputeJournalDecision.Execute;
        }
        else if (claim.Mode == ComputeDispatchMode.Execute && operation.TemplateDigest != claim.TemplateDigest)
            throw new UnauthorizedAccessException("Compute operation template conflicts with execution history.");
        // Persist the fence and receipt before any hypervisor effect. Failure after this write
        // must lead to observation/inventory reconciliation, not another execution claim.
        await WriteAsync(state, token);
        var remaining = claim.ExpiresAt - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) throw new UnauthorizedAccessException("The dispatch expired while recording its fence.");
        using var deadline = new CancellationTokenSource(remaining, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        linked.Token.ThrowIfCancellationRequested();
        var outcome = await effect(decision, environment.ResourceId, linked.Token);
        if (outcome.ResourceId is { } resourceId)
        {
            if (!ValidResourceId(resourceId) || environment.Reservation is null ||
                environment.ResourceId is not null && environment.ResourceId != resourceId)
                throw new InvalidDataException("The physical result conflicts with protected resource ownership.");
            environment.ResourceId = resourceId;
        }
        if (outcome.Observation is { } observation)
            StageResult(state, environment, operation, claim, observation);
        // Preserve completed physical evidence even after cancellation. Identity, sequence and outbox
        // commit together; a failed write leaves the pre-effect claim for observation-only recovery.
        if (outcome.ResourceId is not null || outcome.Observation is not null)
            await WriteAsync(state, CancellationToken.None);
        return outcome.Result;
    }

    /// <summary>Bounded recovery discovery for privileged lease/orphan reconciliation, never agent access.</summary>
    public async Task<IReadOnlyList<ComputePhysicalReservation>> ListReservationsAsync(Guid? afterEnvironmentId,
        int limit, CancellationToken token)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var held = await LockAsync(token);
        var state = await ReadAsync(token);
        return state.Environments.Where(x => x.Value.Reservation is not null &&
                (!afterEnvironmentId.HasValue || x.Key.CompareTo(afterEnvironmentId.Value) > 0))
            .OrderBy(x => x.Key).Take(limit).Select(x => new ComputePhysicalReservation(x.Key,
                x.Value.InstallationId, x.Value.SpecificationDigest, x.Value.Reservation!.Resources,
                x.Value.Reservation.Persistence, x.Value.Reservation.LeaseExpiresAt, x.Value.Generation,
                x.Value.CurrentOperationId, x.Value.DestroyRequested, x.Value.ResourceId, x.Value.Reservation.LeaseExpired)).ToArray();
    }

    /// <summary>Local expiry authority comes only from persisted provisioning terms, never a new agent request.</summary>
    public async Task<bool> EnforceExpiredLeaseAsync(Guid environmentId,
        Func<ComputeLeaseEnforcement, CancellationToken, Task<ComputePhysicalOutcome<bool>>> effect, CancellationToken token,
        Func<ComputeLeaseEnforcement, CancellationToken, Task<bool>>? observeConfirmed = null)
    {
        await using var held = await LockAsync(token);
        var state = await ReadAsync(token);
        if (!state.Environments.TryGetValue(environmentId, out var environment) || environment.Reservation is not { } lease ||
            !lease.LeaseExpired && lease.LeaseExpiresAt > clock.GetUtcNow()) return false;
        var action = lease.Persistence == ComputePersistence.Persistent ? InfrastructureActions.Stop : InfrastructureActions.Destroy;
        var authority = new ComputeLeaseEnforcement(enrollment.NodeId, enrollment.OrganizationId, enrollment.ProviderId,
            environmentId, environment.InstallationId, environment.Generation, environment.ResourceId, action, lease.LeaseExpiresAt);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        // A previous confirmation is only a hint. Re-read exact-owned physical state under the same
        // journal lock before skipping a redundant mutation. Read failure is never proof of absence.
        if (lease.LeaseExpired && lease.LastConfirmedAt is not null && observeConfirmed is not null &&
            await observeConfirmed(authority, linked.Token)) return false;
        lease.LeaseExpired = true;
        lease.LastConfirmedAt = null;
        lease.EnforcementAttempts = checked(lease.EnforcementAttempts + 1);
        lease.LastEnforcementAt = clock.GetUtcNow();
        if (action == InfrastructureActions.Destroy) environment.DestroyRequested = true;
        StageMaintenance(state, environmentId, environment, action, ComputeMaintenancePhase.Requested, false, null);
        // This fence and its audit event precede cleanup and survive helper failure or clock rollback.
        await WriteAsync(state, token);
        ComputePhysicalOutcome<bool> outcome;
        try
        {
            outcome = await effect(authority, linked.Token);
            if (outcome.ResourceId is { } resourceId)
            {
                if (!ValidResourceId(resourceId) || environment.ResourceId is not null && environment.ResourceId != resourceId)
                    throw new InvalidDataException("Lease cleanup returned conflicting physical ownership.");
                environment.ResourceId = resourceId;
            }
        }
        catch
        {
            StageMaintenance(state, environmentId, environment, action, ComputeMaintenancePhase.Failed, false, "lease-cleanup-uncertain");
            await WriteAsync(state, CancellationToken.None);
            throw;
        }
        if (outcome.Result) lease.LastConfirmedAt = clock.GetUtcNow();
        StageMaintenance(state, environmentId, environment, action, ComputeMaintenancePhase.Observed, outcome.Result, null);
        // A failed result write leaves the original request pending; it is not proof that cleanup failed.
        // Confirmation records physical observation only: no capacity or persistent storage is released.
        await WriteAsync(state, CancellationToken.None);
        return true;
    }

    public async Task<IReadOnlyList<ComputeMaintenanceOutboxEntry>> ListMaintenanceEventsAsync(int limit, CancellationToken token)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var held = await LockAsync(token);
        var state = await ReadAsync(token);
        return state.MaintenanceOutbox.Values.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).Take(limit).ToArray();
    }

    public async Task<bool> AcknowledgeMaintenanceEventAsync(Guid eventId, string expectedDigest, CancellationToken token)
    {
        await using var held = await LockAsync(token);
        var state = await ReadAsync(token);
        if (!state.MaintenanceOutbox.TryGetValue(eventId, out var row)) return false;
        if (row.Digest != expectedDigest) throw new InvalidDataException("Maintenance acknowledgement does not match queued evidence.");
        state.MaintenanceOutbox.Remove(eventId);
        await WriteAsync(state, token);
        return true;
    }

    private void StageMaintenance(JournalState state, Guid environmentId, EnvironmentHistory environment, string action,
        ComputeMaintenancePhase phase, bool confirmed, string? failureCode)
    {
        var lease = environment.Reservation!;
        var evidence = new ComputeMaintenanceEvent(Guid.NewGuid(), enrollment.NodeId, enrollment.OrganizationId, enrollment.ProviderId,
            environmentId, environment.InstallationId, lease.ProvisionOperationId, environment.Generation, environment.SpecificationDigest,
            environment.ResourceId, lease.Persistence, lease.LeaseExpiresAt, action, lease.EnforcementAttempts, phase, confirmed,
            failureCode, lease.ProvisionGrants, clock.GetUtcNow());
        var json = JsonSerializer.Serialize(evidence, ComputeProtocol.Json);
        state.MaintenanceOutbox.Add(evidence.EventId, new(evidence.EventId, evidence.OccurredAt, json, ComputeProtocol.Digest(json)));
    }

    private void Reserve(JournalState state, EnvironmentHistory environment, VerifiedComputeDispatch dispatch)
    {
        var reservations = state.Environments.Values.Where(x => x.Reservation is not null && !x.TeardownConfirmed).Select(x => x.Reservation!).ToArray();
        var requested = dispatch.Specification.Resources;
        // Decimal accumulation cannot overflow at the journal's bounded record count, even with long resource units.
        if (!requested.Fits(capacity.Resources) || reservations.Length >= capacity.MaximumEnvironments ||
            reservations.Sum(x => (decimal)x.Resources.CpuCount) + requested.CpuCount > capacity.Resources.CpuCount ||
            reservations.Sum(x => (decimal)x.Resources.MemoryMiB) + requested.MemoryMiB > capacity.Resources.MemoryMiB ||
            reservations.Sum(x => (decimal)x.Resources.DiskMiB) + requested.DiskMiB > capacity.Resources.DiskMiB ||
            reservations.Sum(x => (decimal)x.Resources.GpuCount) + requested.GpuCount > capacity.Resources.GpuCount)
            throw new InvalidOperationException("The provider has insufficient unreserved capacity.");
        environment.Reservation = new()
        {
            Resources = requested, Persistence = dispatch.Specification.Persistence,
            LeaseExpiresAt = dispatch.Authorization.EnvironmentLeaseExpiresAt,
            ProvisionOperationId = dispatch.Authorization.OperationId, ProvisionGrants = dispatch.Authorization.Grants.ToArray()
        };
    }

    private static bool ValidResourceId(string value) => value.Length is > 0 and <= 256 && !value.Any(char.IsControl);

    private string StatePath => Path.Combine(directory, "journal.json");

    private async Task<FileStream> LockAsync(CancellationToken token)
    {
        verifyProtectedPath(directory);
        var path = Path.Combine(directory, "journal.lock");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path)) verifyProtectedPath(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            // The owner holds this lock across provisioning/guest execution. Contention is
            // expected for the entire effect, not evidence of damaged history. The caller's
            // cancellation bounds the wait; actual filesystem failures still fail closed.
            catch (IOException error) when (IsLockContention(error)) { await Task.Delay(25, token); }
        }
    }

    private static bool IsLockContention(IOException error) =>
        (error.HResult & 0xffff) is 32 or 33 ||
        !OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 11;

    private async Task<JournalState> ReadAsync(CancellationToken token)
    {
        verifyProtectedPath(StatePath);
        if (!File.Exists(StatePath) || new FileInfo(StatePath).Length > MaximumBytes)
            throw new InvalidDataException("Protected compute history is missing or oversized; refusing to reset it.");
        var envelope = JsonSerializer.Deserialize<JournalEnvelope>(await File.ReadAllTextAsync(StatePath, token), ComputeProtocol.Json)
            ?? throw new InvalidDataException("Protected compute history is corrupt.");
        if (envelope.Version != 4 || envelope.StateJson is null || envelope.Digest != ComputeProtocol.Digest(envelope.StateJson))
            throw new InvalidDataException("Protected compute history failed integrity validation.");
        var state = JsonSerializer.Deserialize<JournalState>(envelope.StateJson, ComputeProtocol.Json);
        if (state is null || state.NodeId != enrollment.NodeId || state.OrganizationId != enrollment.OrganizationId ||
            state.ProviderId != enrollment.ProviderId || state.Environments is null || state.MaintenanceOutbox is null || state.ResultOutbox is null)
            throw new InvalidDataException("Protected compute history belongs to another enrollment or is invalid.");
        foreach (var environment in state.Environments.Values)
        {
            if (environment is null || environment.Operations is null ||
                environment.ResourceId is { } resourceId && (!ValidResourceId(resourceId) || environment.Reservation is null) ||
                environment.Reservation is { } reservation && (reservation.Resources is not { IsValid: true } ||
                    !Enum.IsDefined(reservation.Persistence) || reservation.LeaseExpiresAt == default || reservation.ProvisionOperationId == Guid.Empty ||
                    reservation.ProvisionGrants is not { Count: > 0 and <= 16 }) ||
                environment.Reservation is null && environment.Operations.Values.Any(x => x.ExecutionClaimed && x.Action == InfrastructureActions.Provision))
                throw new InvalidDataException("Protected compute reservation history is invalid.");
        }
        foreach (var row in state.MaintenanceOutbox)
            if (row.Value is null || row.Key != row.Value.Id || row.Value.EventJson is not { Length: > 0 and <= 32768 } ||
                row.Value.Digest != ComputeProtocol.Digest(row.Value.EventJson))
                throw new InvalidDataException("Protected maintenance outbox is invalid.");
        ValidateResultOutbox(state);
        return state;
    }

    private async Task WriteAsync(JournalState state, CancellationToken token)
    {
        verifyProtectedPath(directory);
        if (File.Exists(StatePath)) verifyProtectedPath(StatePath);
        var json = JsonSerializer.Serialize(state, ComputeProtocol.Json);
        var envelope = new JournalEnvelope(4, json, ComputeProtocol.Digest(json));
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, ComputeProtocol.Json, token);
                if (stream.Length > MaximumBytes) throw new InvalidDataException("The compute journal exceeds its storage budget.");
                await stream.FlushAsync(token); stream.Flush(true);
            }
            File.Move(temporary, StatePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record JournalEnvelope(int Version, string StateJson, string Digest);
    private sealed class JournalState
    {
        public Guid NodeId { get; set; }
        public Guid OrganizationId { get; set; }
        public string ProviderId { get; set; } = "";
        public Dictionary<Guid, EnvironmentHistory> Environments { get; set; } = [];
        public Dictionary<Guid, ComputeResultOutboxEntry> ResultOutbox { get; set; } = [];
        public Dictionary<Guid, ComputeMaintenanceOutboxEntry> MaintenanceOutbox { get; set; } = [];
    }
    private sealed class EnvironmentHistory
    {
        public Guid InstallationId { get; set; }
        public string SpecificationDigest { get; set; } = "";
        public long Generation { get; set; }
        public Guid CurrentOperationId { get; set; }
        public bool DestroyRequested { get; set; }
        public bool TeardownConfirmed { get; set; }
        public Dictionary<Guid, OperationHistory> Operations { get; set; } = [];
        public ReservationHistory? Reservation { get; set; }
        public string? ResourceId { get; set; }
    }
    private sealed class ReservationHistory
    {
        public ComputeResources Resources { get; set; } = null!;
        public Guid ProvisionOperationId { get; set; }
        public IReadOnlyList<ComputeActionAuthorization> ProvisionGrants { get; set; } = [];
        public ComputePersistence Persistence { get; set; }
        public DateTimeOffset LeaseExpiresAt { get; set; }
        public bool LeaseExpired { get; set; }
        public long EnforcementAttempts { get; set; }
        public DateTimeOffset? LastEnforcementAt { get; set; }
        public DateTimeOffset? LastConfirmedAt { get; set; }
    }
    private sealed class OperationHistory
    {
        public long Generation { get; set; }
        public string Action { get; set; } = "";
        public long LastResultSequence { get; set; }
        public bool ExecutionClaimed { get; set; }
        public string? TemplateDigest { get; set; }
    }
}
