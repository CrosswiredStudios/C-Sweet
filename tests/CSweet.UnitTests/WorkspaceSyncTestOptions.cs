using CSweet.Infrastructure.SourceControl;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

internal static class WorkspaceSyncTestOptions
{
    internal static readonly IOptions<WorkspaceSyncTransferOptions> Value = Options.Create(
        new WorkspaceSyncTransferOptions
        {
            MaximumArchiveBytes = 8 * 1024 * 1024,
            MaximumExpandedBytes = 256L * 1024 * 1024,
            MaximumFileCount = 20_000
        });
}
