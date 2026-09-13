using System.Security.Cryptography.X509Certificates;
using CSweet.Compute.Runtime;

namespace CSweet.Compute.HyperV;

/// <summary>Provider process composition with optional installed provisioning settings and maintenance fallback.</summary>
internal static class HyperVMaintenanceHost
{
    public static async Task RunAsync(ComputeProviderConfiguration configuration, Action<string> report, CancellationToken token)
    {
        var clock = TimeProvider.System;
        var catalog = await LoadCatalogAsync(configuration, clock, report, token);
        var journal = new ComputeReplayJournal(configuration.JournalDirectory, configuration.Enrollment, configuration.Capacity, clock);
        var templates = catalog?.Templates ?? new Dictionary<string, CSweet.Domain.Compute.ComputeTemplate>();
        var actions = catalog is null ? Array.Empty<string>() : new[] {
            CSweet.Compute.Contracts.InfrastructureActions.Provision, CSweet.Compute.Contracts.InfrastructureActions.Start,
            CSweet.Compute.Contracts.InfrastructureActions.Stop, CSweet.Compute.Contracts.InfrastructureActions.Restart,
            CSweet.Compute.Contracts.InfrastructureActions.Destroy, CSweet.Compute.Contracts.InfrastructureActions.Execute, CSweet.Compute.Contracts.InfrastructureActions.PublishPort };
        var verifier = new ComputeDispatchVerifier(configuration.Enrollment,
            new(configuration.Enrollment.ProviderId, templates.Keys.ToHashSet(), actions.ToHashSet(),
                catalog is null ? [] : [CSweet.Domain.Compute.ComputeNetworkMode.None], false, catalog is not null),
            configuration.Capacity.Resources, templates, clock);
        var executor = new HyperVComputeExecutor(verifier, journal, new(new HyperVCommandRunner()), configuration.WorkloadDirectory,
            catalog is null ? (_, _) => throw new InvalidOperationException("Provisioning is not configured.") : catalog.OpenAsync,
            catalog is null ? null : HyperVGuestReadinessTransport.ConnectAsync);
        await RunAsync(configuration, executor, clock, ComputeProviderConfigurationLoader.OpenSigningCertificate,
            report, token, enableIntake: catalog is not null);
    }
    internal static async Task<ComputeTemplateCatalog?> LoadCatalogAsync(ComputeProviderConfiguration configuration,
        TimeProvider clock, Action<string> report, CancellationToken token,
        Func<string, ComputeProviderConfiguration, TimeProvider, CancellationToken, Task<ComputeTemplateCatalog>>? read = null)
    {
        if (configuration.ProvisioningSettingsPath is not { } path) return null;
        try { return await (read ?? ComputeProvisioningSettingsLoader.ReadCatalogAsync)(path, configuration, clock, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or
            System.Security.Cryptography.CryptographicException or ArgumentException)
        { report("provisioning-configuration-unavailable"); return null; }
    }
    internal static async Task RunAsync(ComputeProviderConfiguration configuration, HyperVComputeExecutor executor,
        TimeProvider clock, Func<ComputeProviderConfiguration, TimeProvider, X509Certificate2> openCertificate,
        Action<string> report, CancellationToken token, bool enableIntake = false)
    {
        executor.AuthorizePublication = async (operationId, cancellation) =>
        {
            using var permissionConnection = new HyperVWorkConnection(configuration, executor.DispatchVerifier, clock, openCertificate);
            return await permissionConnection.AuthorizePublicationAsync(operationId, cancellation);
        };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var connection = new HyperVMaintenanceDeliveryConnection(configuration, clock, openCertificate);
        using var resultConnection = new HyperVResultDeliveryConnection(configuration, clock, openCertificate);
        using var workConnection = new HyperVWorkConnection(configuration, executor.DispatchVerifier, clock, openCertificate);
        using var intakeConnection = enableIntake ? new HyperVWorkConnection(configuration, executor.DispatchVerifier, clock, openCertificate) : null;
        using var notificationConnection = enableIntake ? new HyperVWorkConnection(configuration, executor.DispatchVerifier, clock, openCertificate) : null;
        var leases = executor.CreateLeaseMonitor(clock, pass =>
        {
            if (pass.Failed > 0) report("lease-maintenance-failed");
        });
        var retrying = false;
        var delivery = executor.CreateMaintenanceDeliveryWorker(connection.DeliverAsync, clock, pass =>
        {
            if (pass.ConsecutiveFailures == 1) { retrying = true; report("maintenance-delivery-retrying"); }
            else if (retrying && pass.ConsecutiveFailures == 0)
            { retrying = false; report("maintenance-delivery-recovered"); }
        });
        var recoveryRetrying = false;
        var recovery = executor.CreateObservationRecoveryWorker(workConnection.ClaimAsync, clock, pass =>
        {
            if (pass.ConsecutiveFailures == 1) { recoveryRetrying = true; report("observation-recovery-retrying"); }
            else if (recoveryRetrying && pass.ConsecutiveFailures == 0)
            { recoveryRetrying = false; report("observation-recovery-recovered"); }
        }, workConnection.ReadResultReceiptAsync);
        var resultRetrying = false;
        var results = executor.CreateResultDeliveryWorker(resultConnection.DeliverAsync, clock, pass =>
        {
            if (pass.ConsecutiveFailures == 1) { resultRetrying = true; report("result-delivery-retrying"); }
            else if (resultRetrying && pass.ConsecutiveFailures == 0)
            { resultRetrying = false; report("result-delivery-recovered"); }
            if (pass.Delivery?.RequiresObservation.Count > 0) recovery.NotifyStateChanged();
        });
        var intakeRetrying = false;
        var intake = intakeConnection is null ? null : new ComputeDispatchIntakeWorker(new(executor.DispatchVerifier),
            intakeConnection.DiscoverAsync, intakeConnection.ClaimAsync,
            async (packet, cancellation) => { await executor.ExecuteAsync(packet, cancellation); }, clock, pass =>
            {
                if (pass.ConsecutiveFailures == 1) { intakeRetrying = true; report("dispatch-intake-retrying"); }
                else if (intakeRetrying && pass.ConsecutiveFailures == 0)
                { intakeRetrying = false; report("dispatch-intake-recovered"); }
            });
        var notificationRetrying = false;
        var notifications = intake is null ? null : new ComputeProviderNotificationWorker(notificationConnection!.WaitForWakeAsync,
            intake.NotifyWorkAvailable, clock, pass =>
            {
                if (pass.ConsecutiveFailures == 1) { notificationRetrying = true; report("provider-notification-retrying"); }
                else if (notificationRetrying && pass.ConsecutiveFailures == 0)
                { notificationRetrying = false; report("provider-notification-recovered"); }
            });
        var supervisor = new ComputeProviderMaintenanceSupervisor(leases, delivery, results, recovery, intake, notifications);
        Task? running = null;
        try
        {
            running = supervisor.RunAsync(lifetime.Token);
            var signals = new List<Task> { running, supervisor.DeliveryFailure, supervisor.ResultDeliveryFailure, supervisor.ObservationRecoveryFailure };
            if (intake is not null) { signals.Add(supervisor.DispatchIntakeFailure); signals.Add(supervisor.NotificationFailure); }
            while (signals.Count > 1)
            {
                var completed = await Task.WhenAny(signals);
                if (completed == running) break;
                signals.Remove(completed);
                if (completed.IsCompletedSuccessfully)
                    report(completed == supervisor.DeliveryFailure ? "maintenance-delivery-stopped" :
                        completed == supervisor.ResultDeliveryFailure ? "result-delivery-stopped" :
                        completed == supervisor.DispatchIntakeFailure ? "dispatch-intake-stopped" :
                        completed == supervisor.NotificationFailure ? "provider-notification-stopped" : "observation-recovery-stopped");
            }
            await running;
        }
        finally
        {
            executor.ClosePublishedPorts();
            lifetime.Cancel();
            if (running is not null) { try { await running; } catch (Exception) { } }
            // Join all workers even if diagnostic reporting itself failed.
            // The connection is disposed by its using scope after this join.
        }
    }
}
