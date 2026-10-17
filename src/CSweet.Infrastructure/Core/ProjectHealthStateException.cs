namespace CSweet.Infrastructure.Core;

public sealed class ProjectHealthStateException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
