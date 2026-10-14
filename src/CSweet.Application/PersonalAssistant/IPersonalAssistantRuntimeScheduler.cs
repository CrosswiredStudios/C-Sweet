namespace CSweet.Application.PersonalAssistant;

public interface IPersonalAssistantRuntimeScheduler
{
    Task WakeAsync(Guid installationId, CancellationToken token);
}
