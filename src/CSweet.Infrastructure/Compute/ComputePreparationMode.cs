using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CSweet.Infrastructure.Compute;

public sealed record ComputePreparationMode(bool LegacyInteractive)
{
    public static ComputePreparationMode FromHost(IHostEnvironment environment, IConfiguration configuration) =>
        new(OperatingSystem.IsWindows() && environment.IsDevelopment() && Environment.UserInteractive &&
            configuration.GetValue<bool>("CSweet:Compute:EnableLegacyInteractiveSetup"));
}
