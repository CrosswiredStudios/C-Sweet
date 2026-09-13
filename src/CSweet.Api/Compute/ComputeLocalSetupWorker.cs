using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Compute;

/// <summary>Owns local installer dispatch. An agent can request compute, never supply installer arguments.</summary>
public sealed class ComputeLocalSetupWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    IWebHostEnvironment environment, ILogger<ComputeLocalSetupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Local compute preparation could not advance."); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task PassAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        // A crash before the handoff was written cannot have launched an installer.
        // Recover that preparation automatically; never relaunch an existing handoff.
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var interrupted in await db.Set<ComputeLocalSetup>().AsNoTracking()
                     .Where(x => x.State == "Running" && x.UpdatedAt < cutoff).Take(100).ToListAsync(token))
        {
            var observation = ComputeSetupObservation.Read(interrupted, Path.GetDirectoryName(HandoffPath(interrupted.Id))!);
            if (observation.FailureCode is { } failure)
            {
                await FailAsync(db, interrupted.Id, failure, token);
                continue;
            }
            if (File.Exists(HandoffPath(interrupted.Id))) continue;
            await db.Set<ComputeLocalSetup>().Where(x => x.Id == interrupted.Id && x.State == "Running" && x.Revision == interrupted.Revision)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Pending").SetProperty(x => x.HandoffHash, (string?)null)
                    .SetProperty(x => x.HandoffExpiresAt, (DateTimeOffset?)null).SetProperty(x => x.Revision, x => x.Revision + 1)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), token);
        }
        // Recover a lost installer or declined UAC without repeatedly launching administrator prompts.
        await db.Set<ComputeLocalSetup>().Where(x => x.State == "Running" && x.HandoffExpiresAt < DateTimeOffset.UtcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Failed").SetProperty(x => x.ErrorCode, "setup_interrupted")
                .SetProperty(x => x.HandoffHash, (string?)null), token);
        var defaults = scope.ServiceProvider.GetRequiredService<ComputeDefaultsService>();
        foreach (var ready in await db.Set<ComputeLocalSetup>().Where(x => x.State == "Ready").Take(100).ToListAsync(token))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
            await defaults.ActivateAccessAsync(ready, token);
            await RequestDockerUpgradeAsync(db, ready, token);
            await transaction.CommitAsync(token);
        }
        var setup = await db.Set<ComputeLocalSetup>().AsNoTracking().Where(x => x.State == "Pending").OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(token);
        if (setup is null) return;
        if (!OperatingSystem.IsWindows()) { await FailAsync(db, setup.Id, "local_provider_unavailable", token); return; }
        var root = configuration["CSweet:Compute:RepositoryRoot"] ?? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
        var script = Path.Combine(root, "scripts", "Install-ComputeLocalProvider.ps1");
        var origin = configuration["CSweet:ExecutionGateway:PublicUrl"];
        var pin = configuration["CSweet:ExecutionGateway:PublicCertificateSha256"];
        if (!File.Exists(script) || !Uri.TryCreate(origin, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
            pin is not { Length: 64 } || !pin.All(Uri.IsHexDigit))
        { await FailAsync(db, setup.Id, "setup_components_unavailable", token); return; }
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        var now = DateTimeOffset.UtcNow;
        if (await db.Set<ComputeLocalSetup>().Where(x => x.Id == setup.Id && x.State == "Pending").ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.State, "Running").SetProperty(x => x.HandoffHash, hash)
                .SetProperty(x => x.HandoffExpiresAt, now.AddHours(2)).SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.Revision, x => x.Revision + 1), token) != 1) return;
        var handoff = HandoffPath(setup.Id);
        var directory = Path.GetDirectoryName(handoff)!;
        try
        {
            Directory.CreateDirectory(directory);
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(acl);
            var signing = await scope.ServiceProvider.GetRequiredService<IComputeDispatchSigner>().GetIdentityAsync(token);
            await File.WriteAllTextAsync(handoff, JsonSerializer.Serialize(new { version = 1, setupId = setup.Id,
                organizationId = setup.OrganizationId, secret, coreOrigin = endpoint.GetLeftPart(UriPartial.Authority) + "/",
                coreCertificateSha256 = pin, controlPlaneKey = signing, repositoryRoot = root }), token);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -HandoffPath \"{handoff}\"",
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new IOException("The local installer did not start.");
            await process.WaitForExitAsync(token);
            var completed = await db.Set<ComputeLocalSetup>().AsNoTracking().SingleAsync(x => x.Id == setup.Id, token);
            if (completed.State != "Ready") await FailAsync(db, setup.Id, "local_setup_failed", token);
        }
        catch (System.ComponentModel.Win32Exception) { await FailAsync(db, setup.Id, "administrator_approval_required", token); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Local compute setup {SetupId} failed.", setup.Id);
            await FailAsync(db, setup.Id, "local_setup_failed", token);
        }
        finally { if (!token.IsCancellationRequested && File.Exists(handoff)) File.Delete(handoff); }
    }

    private static string HandoffPath(Guid id) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSweet", "ComputeSetup", id.ToString("N"), "handoff.json");

    internal static async Task RequestDockerUpgradeAsync(CSweetDbContext db, ComputeLocalSetup setup, CancellationToken ct)
    {
        var template = await db.ComputeTemplates.SingleOrDefaultAsync(x => x.OrganizationId == setup.OrganizationId && x.TemplateId == setup.TemplateId, ct);
        if (template is null || JsonSerializer.Deserialize<ComputeTemplate>(template.TemplateJson, CSweet.Compute.Contracts.ComputeProtocol.Json)?.Features.Contains("docker") == true) return;
        var business = setup.OrganizationId.ToString("D");
        var approvals = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).Where(x => x.BusinessId == business && x.IsEnabled &&
            x.RevisionStatus == CSweet.Domain.Setup.PluginRevisionStatus.Active).ToListAsync(ct);
        if (!approvals.Any(x => (JsonSerializer.Deserialize<HashSet<string>>(x.Grant?.RequiredCapabilitiesJson ?? "[]") ?? []).Contains("source-control.personal-work.prepare.v1"))) return;
        // The image upgrade must not interrupt live resources.
        if (await db.ComputeEnvironments.AnyAsync(x => x.OrganizationId == setup.OrganizationId && x.TeardownConfirmedAt == null, ct)) return;
        template.Enabled = false; template.Revision++;
        setup.State = "Pending"; setup.HandoffHash = null; setup.HandoffExpiresAt = null; setup.ErrorCode = null;
        setup.UpdatedAt = DateTimeOffset.UtcNow; setup.Revision++;
        foreach (var access in await db.Set<ComputeAgentAccess>().Where(x => x.SetupId == setup.Id).ToListAsync(ct)) access.GrantsCreatedAt = null;
        await db.SaveChangesAsync(ct);
    }

    private static Task<int> FailAsync(CSweetDbContext db, Guid id, string code, CancellationToken token) =>
        db.Set<ComputeLocalSetup>().Where(x => x.Id == id && x.State != "Ready").ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.State, "Failed").SetProperty(x => x.ErrorCode, code)
                .SetProperty(x => x.HandoffHash, (string?)null).SetProperty(x => x.HandoffExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.Revision, x => x.Revision + 1).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), token);
}
