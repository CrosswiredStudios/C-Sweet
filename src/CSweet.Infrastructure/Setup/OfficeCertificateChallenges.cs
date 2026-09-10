using System.Security.Cryptography;
using CSweet.Office.Contracts.ControlPlane;

namespace CSweet.Infrastructure.Setup;

/// <summary>Bounded one-use challenges. Restarts fail closed; clients request a new challenge.</summary>
public sealed class OfficeCertificateChallenges(TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (Guid OfficeId, DateTimeOffset ExpiresAt)> _pending = [];

    public OfficeCertificateChallengeResponse? Create(Guid officeId)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            foreach (var key in _pending.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
                _pending.Remove(key);
            if (_pending.Count >= 1024) return null;
            var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var expiresAt = now.AddMinutes(2);
            _pending.Add(challenge, (officeId, expiresAt));
            return new(challenge, expiresAt);
        }
    }

    public bool Consume(Guid officeId, string? challenge)
    {
        if (challenge is null || challenge.Length != 44) return false;
        lock (_gate)
            return _pending.Remove(challenge, out var pending) &&
                pending.OfficeId == officeId && pending.ExpiresAt > clock.GetUtcNow();
    }
}
