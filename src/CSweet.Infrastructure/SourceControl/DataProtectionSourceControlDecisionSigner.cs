using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.SourceControl;
using Microsoft.AspNetCore.DataProtection;

namespace CSweet.Infrastructure.SourceControl;

public sealed class DataProtectionSourceControlDecisionSigner(
    IDataProtectionProvider dataProtectionProvider) : ISourceControlDecisionSigner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "CSweet.SourceControl.MergeDecision.v2");

    public string Sign(SourceControlMergeDecision decision) =>
        _protector.Protect(JsonSerializer.Serialize(decision, JsonOptions));

    public bool Verify(SourceControlMergeDecision decision, string signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        try
        {
            var signed = JsonSerializer.Deserialize<SourceControlMergeDecision>(
                _protector.Unprotect(signature), JsonOptions);
            return signed == decision;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return false;
        }
    }
}
