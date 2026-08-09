using CSweet.Application.WorkManagement;

namespace CSweet.AgentHost.Broker;

public sealed class PersonalTodoReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PersonalTodoReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var personalTodo = scope.ServiceProvider.GetRequiredService<IPersonalTodoService>();
                await personalTodo.ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Personal to-do provisioning and lease reconciliation failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
