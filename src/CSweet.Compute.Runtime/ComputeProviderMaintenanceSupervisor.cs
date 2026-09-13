namespace CSweet.Compute.Runtime;

public enum ComputeMaintenanceServiceFailure { DeliveryStopped, RecoveryStopped, IntakeStopped, NotificationStopped }

/// <summary>Delivery and recovery failures are independent of local lease enforcement. All lanes join on shutdown.</summary>
public sealed class ComputeProviderMaintenanceSupervisor(
    ComputeLeaseMonitor leases, ComputeMaintenanceDeliveryWorker delivery, ComputeResultDeliveryWorker? results = null,
    ComputeObservationRecoveryWorker? recovery = null, ComputeDispatchIntakeWorker? intake = null, ComputeProviderNotificationWorker? notifications = null)
{
    private readonly TaskCompletionSource<ComputeMaintenanceServiceFailure> deliveryFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ComputeMaintenanceServiceFailure> resultFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ComputeMaintenanceServiceFailure> recoveryFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<ComputeMaintenanceServiceFailure> DeliveryFailure => deliveryFailure.Task;
    public Task<ComputeMaintenanceServiceFailure> ResultDeliveryFailure => resultFailure.Task;
    public Task<ComputeMaintenanceServiceFailure> ObservationRecoveryFailure => recoveryFailure.Task;
    private readonly TaskCompletionSource<ComputeMaintenanceServiceFailure> intakeFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<ComputeMaintenanceServiceFailure> DispatchIntakeFailure => intakeFailure.Task;
    private readonly TaskCompletionSource<ComputeMaintenanceServiceFailure> notificationFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<ComputeMaintenanceServiceFailure> NotificationFailure => notificationFailure.Task;
    private int started;

    public async Task RunAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
            throw new InvalidOperationException("This maintenance supervisor has already started.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sending = Start(delivery.RunAsync, deliveryFailure, ComputeMaintenanceServiceFailure.DeliveryStopped);
        var resultSending = results is null ? Task.CompletedTask : Start(results.RunAsync, resultFailure, ComputeMaintenanceServiceFailure.DeliveryStopped);
        var observing = recovery is null ? Task.CompletedTask : Start(recovery.RunAsync, recoveryFailure, ComputeMaintenanceServiceFailure.RecoveryStopped);
        var receiving = intake is null ? Task.CompletedTask : Start(intake.RunAsync, intakeFailure, ComputeMaintenanceServiceFailure.IntakeStopped);
        var listening = notifications is null ? Task.CompletedTask : Start(notifications.RunAsync, notificationFailure, ComputeMaintenanceServiceFailure.NotificationStopped);
        try
        {
            await leases.RunAsync(lifetime.Token);
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Provider lease enforcement stopped unexpectedly.");
        }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(sending, resultSending, observing, receiving, listening);
            deliveryFailure.TrySetCanceled(); resultFailure.TrySetCanceled(); recoveryFailure.TrySetCanceled(); intakeFailure.TrySetCanceled(); notificationFailure.TrySetCanceled();
        }

        Task Start(Func<CancellationToken, Task> run, TaskCompletionSource<ComputeMaintenanceServiceFailure> failure, ComputeMaintenanceServiceFailure code) =>
            Task.Run(async () =>
            {
                try
                {
                    await run(lifetime.Token);
                    if (!lifetime.IsCancellationRequested) failure.TrySetResult(code);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception) { failure.TrySetResult(code); }
            }, CancellationToken.None);
    }
}