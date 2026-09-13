using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;

namespace CSweet.Compute.HyperV;

/// <summary>
/// Privileged local composition, not an agent API. Transport, signing, lease enforcement and
/// certified teardown must be integrated before exposing this as an operational provider.
/// Raw VM state is not guest readiness or confirmed storage teardown.
/// </summary>
internal sealed class HyperVComputeExecutor
{
    private readonly ComputeDispatchVerifier verifier;
    private readonly ComputeReplayJournal journal;
    private readonly HyperVComputeDriver driver;
    private readonly string root;
    private readonly Func<VerifiedComputeDispatch, CancellationToken, Task<ComputeCertifiedPayload>> openPayload;
    private readonly Action<string> verifyProtectedPath;
    private readonly Action<string, string, Guid> verifyWorkloadPath;
    private readonly Func<Guid, CancellationToken, Task<Stream>>? connectGuest;
    private readonly ComputeLocalPortPublisher publisher = new();
    internal Func<Guid, CancellationToken, Task<bool>>? AuthorizePublication { get; set; }
    public void ClosePublishedPorts() => publisher.Dispose();
    private ComputeLeaseMonitor? leaseMonitor;
    private ComputeMaintenanceDeliveryWorker? maintenanceDelivery;
    private ComputeResultDeliveryWorker? resultDelivery;
    private ComputeObservationRecoveryWorker? observationRecovery;
    internal ComputeDispatchVerifier DispatchVerifier => verifier;

    public HyperVComputeExecutor(ComputeDispatchVerifier verifier, ComputeReplayJournal journal,
        HyperVComputeDriver driver, string workloadDirectory,
        Func<VerifiedComputeDispatch, CancellationToken, Task<ComputeCertifiedPayload>> openPayload,
        Func<Guid, CancellationToken, Task<Stream>>? connectGuest = null)
        : this(verifier, journal, driver, workloadDirectory, openPayload, WindowsComputeProtectedPaths.Verify, connectGuest)
        { verifyWorkloadPath = HyperVWorkloadProtection.Verify; }

    internal HyperVComputeExecutor(ComputeDispatchVerifier verifier, ComputeReplayJournal journal,
        HyperVComputeDriver driver, string workloadDirectory,
        Func<VerifiedComputeDispatch, CancellationToken, Task<ComputeCertifiedPayload>> openPayload,
        Action<string> protection, Func<Guid, CancellationToken, Task<Stream>>? connectGuest = null)
    {
        if (!Path.IsPathFullyQualified(workloadDirectory)) throw new ArgumentException("An absolute protected workload root is required.");
        this.verifier = verifier; this.journal = journal; this.driver = driver;
        root = Path.GetFullPath(workloadDirectory); this.openPayload = openPayload; verifyProtectedPath = protection;
        verifyWorkloadPath = (path, _, _) => protection(path);
        this.connectGuest = connectGuest;
        verifyProtectedPath(root);
    }

    public async Task<HyperVMachineObservation> ExecuteAsync(ComputeDispatchPacket packet, CancellationToken token)
    {
        var dispatch = verifier.Verify(packet);
        var verifiedPacket = new ComputeDispatchPacket(packet.Authorization, dispatch.Specification, dispatch.Template, dispatch.Workload);
        try
        {
        return await journal.RunPhysicalAsync(dispatch, async (decision, knownResourceId, cancellation) =>
        {
            var claim = dispatch.Authorization;
            var identity = new HyperVMachineIdentity(claim.NodeId, claim.OrganizationId, claim.InstallationId, claim.EnvironmentId);
            var environmentDirectory = Path.Combine(root, claim.EnvironmentId.ToString("N"));
            var privateDisk = Path.Combine(environmentDirectory, "os.vhdx");
            HyperVMachineObservation observation;
            var provisionStarted = false;
            try
            {
            if (claim.Action is InfrastructureActions.Stop or InfrastructureActions.Restart or InfrastructureActions.Destroy) publisher.Remove(claim.EnvironmentId);
            if (claim.Action is InfrastructureActions.Execute or InfrastructureActions.PublishPort)
            {
                observation = knownResourceId is null ? await driver.DiscoverAsync(identity, cancellation) :
                    await driver.ObserveAsync(identity, ParseId(knownResourceId), cancellation);
                ComputeWorkloadResult result;
                if (decision == ComputeJournalDecision.Observe)
                    result = new(ErrorCode: "outcome-unknown");
                else if (observation is not { Id: { }, State: "Running" } || connectGuest is null)
                    result = new(ErrorCode: "guest-unavailable");
                else
                {
                    try
                    {
                        var vm = observation.Id.Value;
                        if (dispatch.Workload!.Command is { } command)
                            result = new(Command: await ComputeGuestCommandClient.ExecuteAsync(ct => connectGuest(vm, ct), command,
                                dispatch.Specification.OperatingSystem, cancellation));
                        else
                            result = await publisher.PublishAsync(claim.EnvironmentId, dispatch.Workload.PublishPort!.Value,
                                                                claim.EnvironmentLeaseExpiresAt, async ct =>
                                {
                                    if (AuthorizePublication is null || !await AuthorizePublication(claim.OperationId, ct))
                                        throw new IOException("Publication authority is unavailable.");
                                    var owned = await driver.ObserveAsync(identity, vm, ct);
                                    if (owned.State != "Running") throw new IOException("Owned VM is not running.");
                                    return await connectGuest(vm, ct);
                                }, cancellation);
                    }
                    catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Text.Json.JsonException)
                    { result = new(ErrorCode: "outcome-unknown"); }
                }
                return new ComputePhysicalOutcome<HyperVMachineObservation>(observation, observation.Id?.ToString("D"),
                    new(observation.State == "Running" ? ComputeLifecycleState.Ready : ComputeLifecycleState.Failed, Workload: result));
            }
            if (decision == ComputeJournalDecision.Observe)
            {
                observation = knownResourceId is null ? await driver.DiscoverAsync(identity, cancellation) :
                    await driver.ObserveAsync(identity, ParseId(knownResourceId), cancellation);
            }
            else if (claim.Action == InfrastructureActions.Provision)
            {
                // The resolver is installer-owned. It must verify release evidence and complete materialized
                // payloads; agents cannot select a host path or supply their own resolver.
                using var payload = await openPayload(dispatch, cancellation);
                verifyProtectedPath(root);
                if (Path.Exists(environmentDirectory))
                    throw new InvalidDataException("Existing workload files require reconciliation before provisioning.");
                Directory.CreateDirectory(environmentDirectory); verifyProtectedPath(environmentDirectory);
                var machinePath = Path.Combine(environmentDirectory, "machine");
                Directory.CreateDirectory(machinePath); verifyProtectedPath(machinePath);
                verifier.Verify(verifiedPacket);
                provisionStarted = true;
                observation = await driver.CreateAsync(identity, dispatch.Specification, machinePath, privateDisk, payload.ImagePath, cancellation);
                // Provisioning includes initial activation. Recheck the signed window after image
                // materialization and VM creation, then use the same topology guard as explicit start.
                // A lost response leaves the durable execution fence; replay only discovers/observes.
                verifier.Verify(verifiedPacket);
                verifyWorkloadPath(privateDisk, environmentDirectory, observation.Id!.Value);
                observation = await driver.ApplyAsync(identity, observation.Id!.Value, InfrastructureActions.Start,
                    cancellation, dispatch.Specification, privateDisk);
            }
            else
            {
                // Unknown identity is discovered by the complete ownership marker, never trusted from a request.
                var current = knownResourceId is null ? await driver.DiscoverAsync(identity, cancellation) :
                    new HyperVMachineObservation(ParseId(knownResourceId), "Unknown");
                if (current.Id is null) observation = current;
                else
                {
                    if (claim.Action is InfrastructureActions.Start or InfrastructureActions.Restart)
                    {
                        verifyWorkloadPath(privateDisk, environmentDirectory, current.Id.Value);
                    }
                    observation = await driver.ApplyAsync(identity, current.Id.Value, claim.Action, cancellation,
                        dispatch.Specification, privateDisk);
                }
            }
            var lifecycle = LifecycleObservation(claim.Action, observation);
            if (claim.Action == InfrastructureActions.Destroy && observation.State == "Missing")
            {
                if (decision == ComputeJournalDecision.Execute) RemoveWorkloadDirectory(claim.EnvironmentId, observation.Id);
                if (!Directory.Exists(environmentDirectory)) lifecycle = new(ComputeLifecycleState.Destroyed, TeardownConfirmed: true);
            }
            if (connectGuest is not null && observation is { Id: { } vmId, State: "Running" } &&
                claim.Action is InfrastructureActions.Provision or InfrastructureActions.Start or InfrastructureActions.Restart)
            {
                var ready = await ComputeGuestReadiness.ProbeAsync(ct => connectGuest(vmId, ct),
                    dispatch.Specification.OperatingSystem, dispatch.Specification.Architecture, cancellation);
                if (ready)
                {
                    // The guest cannot establish physical identity or override a VM that stopped during the probe.
                    observation = await driver.ObserveAsync(identity, vmId, cancellation);
                    lifecycle = observation.State == "Running" ? new(ComputeLifecycleState.Ready) : LifecycleObservation(claim.Action, observation);
                }
            }
            return new ComputePhysicalOutcome<HyperVMachineObservation>(observation, observation.Id?.ToString("D"), lifecycle);
            }
            catch (Exception error) when (provisionStarted && !cancellation.IsCancellationRequested &&
                error is IOException or UnauthorizedAccessException or TimeoutException)
            {
                // The execution fence is already durable. Record failure atomically with its
                // notification evidence; never replay an uncertain physical effect.
                var resourceId = knownResourceId;
                try { resourceId = (await driver.DiscoverAsync(identity, cancellation)).Id?.ToString("D") ?? resourceId; }
                catch (Exception readError) when (readError is IOException or UnauthorizedAccessException or TimeoutException) { }
                return new ComputePhysicalOutcome<HyperVMachineObservation>(
                    new(resourceId is null ? null : ParseId(resourceId), "Unknown"), resourceId,
                    new(ComputeLifecycleState.Failed, FailureCode: "provider-operation-failed"));
            }
        }, token);
        }
        finally { Volatile.Read(ref leaseMonitor)?.NotifyStateChanged(); Volatile.Read(ref resultDelivery)?.NotifyStateChanged(); }
    }

    private static ComputeLifecycleObservation LifecycleObservation(string action, HyperVMachineObservation observation)
    {
        // VM absence is not proof that its disk was removed. Running is not guest readiness.
        if (action == InfrastructureActions.Destroy) return new(ComputeLifecycleState.Destroying);
        if (observation.Id is null || observation.State == "Missing") return new(ComputeLifecycleState.Failed, FailureCode: "resource-not-found");
        if (action == InfrastructureActions.Stop)
            return new(observation.State == "Off" ? ComputeLifecycleState.Stopped : ComputeLifecycleState.Stopping);
        if (observation.State == "Off") return new(ComputeLifecycleState.Failed, FailureCode: "activation-incomplete");
        return new(observation.State == "Running" ? ComputeLifecycleState.Bootstrapping : ComputeLifecycleState.Provisioning);
    }
    public ComputeLeaseMonitor CreateLeaseMonitor(TimeProvider clock, Action<ComputeLeaseMonitorPass> report)
    {
        var monitor = new ComputeLeaseMonitor(journal, EnforceExpiredLeaseAsync, clock, report);
        if (Interlocked.CompareExchange(ref leaseMonitor, monitor, null) is not null)
            throw new InvalidOperationException("A lease monitor is already attached to this executor.");
        return monitor;
    }

    public ComputeMaintenanceDeliveryWorker CreateMaintenanceDeliveryWorker(
        Func<ComputeMaintenanceOutboxEntry, CancellationToken, Task> deliver, TimeProvider clock,
        Action<ComputeMaintenanceDeliveryPass> report)
    {
        var worker = new ComputeMaintenanceDeliveryWorker(journal, deliver, clock, report);
        if (Interlocked.CompareExchange(ref maintenanceDelivery, worker, null) is not null)
            throw new InvalidOperationException("A maintenance delivery worker is already attached to this executor.");
        return worker;
    }

    public ComputeResultDeliveryWorker CreateResultDeliveryWorker(
        Func<ComputeResultOutboxEntry, CancellationToken, Task<ComputeResultAcknowledgement>> deliver, TimeProvider clock,
        Action<ComputeResultWorkerPass> report)
    {
        var worker = new ComputeResultDeliveryWorker(journal, deliver, clock, report);
        if (Interlocked.CompareExchange(ref resultDelivery, worker, null) is not null)
            throw new InvalidOperationException("A result delivery worker is already attached to this executor.");
        return worker;
    }
    public ComputeObservationRecoveryWorker CreateObservationRecoveryWorker(
        Func<Guid, CancellationToken, Task<ComputeDispatchPacket?>> claim, TimeProvider clock, Action<ComputeObservationWorkerPass> report,
        Func<ComputeResultOutboxEntry, CancellationToken, Task<ComputeResultAcknowledgement?>>? readReceipt = null)
    {
        var worker = new ComputeObservationRecoveryWorker(new(journal, verifier, clock), claim,
            async (packet, token) => { await ExecuteAsync(packet, token); }, clock, report, readReceipt: readReceipt);
        if (Interlocked.CompareExchange(ref observationRecovery, worker, null) is not null)
            throw new InvalidOperationException("An observation recovery worker is already attached to this executor.");
        return worker;
    }
    public async Task<bool> EnforceExpiredLeaseAsync(Guid environmentId, CancellationToken token)
    {
        try
        {
            return await journal.EnforceExpiredLeaseAsync(environmentId, async (authority, cancellation) =>
            {
                var identity = new HyperVMachineIdentity(authority.NodeId, authority.OrganizationId, authority.InstallationId, authority.EnvironmentId);
                var current = authority.ResourceId is null ? await driver.DiscoverAsync(identity, cancellation) :
                    new HyperVMachineObservation(ParseId(authority.ResourceId), "Unknown");
                if (current.Id is { } id) current = await driver.ApplyAsync(identity, id, authority.Action, cancellation);
                publisher.Remove(environmentId);
                if (authority.Action == InfrastructureActions.Destroy && current.State == "Missing") RemoveWorkloadDirectory(environmentId, current.Id);
                return new ComputePhysicalOutcome<bool>(IsEnforced(authority, current), current.Id?.ToString("D"));
            }, token, async (authority, cancellation) =>
            {
                var identity = new HyperVMachineIdentity(authority.NodeId, authority.OrganizationId, authority.InstallationId, authority.EnvironmentId);
                var current = authority.ResourceId is null ? await driver.DiscoverAsync(identity, cancellation) :
                    await driver.ObserveAsync(identity, ParseId(authority.ResourceId), cancellation);
                return IsEnforced(authority, current);
            });
        }
        finally { Volatile.Read(ref maintenanceDelivery)?.NotifyStateChanged(); }
    }

    private void RemoveWorkloadDirectory(Guid environmentId, Guid? vmId)
    {
        var path = Path.GetFullPath(Path.Combine(root, environmentId.ToString("N")));
        if (environmentId == Guid.Empty || Path.GetDirectoryName(path) != root) throw new IOException("Invalid cleanup root.");
        verifyProtectedPath(root);
        if (!Directory.Exists(path)) return;
        VerifyTree(path);
        Directory.Delete(path, recursive: true);
        if (Directory.Exists(path)) throw new IOException("Workload cleanup was not confirmed.");
        void VerifyTree(string directory)
        {
            VerifyEntry(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked workload path.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                VerifyEntry(entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked workload entry.");
                if ((attributes & FileAttributes.Directory) != 0) VerifyTree(entry);
            }
        }
        void VerifyEntry(string entry)
        {
            if (vmId is { } id) verifyWorkloadPath(entry, path, id);
            else verifyProtectedPath(entry);
        }
    }
    private static bool IsEnforced(ComputeLeaseEnforcement authority, HyperVMachineObservation current) =>
        authority.Action == InfrastructureActions.Destroy ? current.State == "Missing" : current.State is "Off" or "Missing";
    private static Guid ParseId(string resourceId) => Guid.TryParseExact(resourceId, "D", out var id) && id != Guid.Empty
        ? id : throw new InvalidDataException("The protected Hyper-V identity is invalid.");
}
