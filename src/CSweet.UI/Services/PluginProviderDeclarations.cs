using CSweet.Contracts.Plugins;

namespace CSweet.UI.Services;

/// <summary>Projects public package metadata for administration, never approval or credential authority.</summary>
public static class PluginProviderDeclarations
{
    public sealed record Requirement(string Id, OAuthProviderMetadata? Provider, bool Conflicting)
    {
        public bool CanConfigure => Provider is not null && !Conflicting;

        public bool Matches(PluginProviderProfileResponse profile) => CanConfigure &&
            string.Equals(Id, profile.Id, StringComparison.Ordinal) &&
            string.Equals(Provider!.AuthorizationEndpoint, profile.AuthorizationEndpoint, StringComparison.Ordinal) &&
            string.Equals(Provider.TokenEndpoint, profile.TokenEndpoint, StringComparison.Ordinal) &&
            string.Equals(Provider.RevocationEndpoint ?? string.Empty, profile.RevocationEndpoint ?? string.Empty, StringComparison.Ordinal);
    }

    public static IReadOnlyList<Requirement> Collect(IEnumerable<PluginConnectionDeclaration> declarations) =>
        declarations.Where(x => !string.IsNullOrWhiteSpace(x.ProviderProfile))
            .GroupBy(x => x.ProviderProfile, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var providers = group.Select(x => x.Provider).ToArray();
                var first = providers[0];
                // Missing metadata and conflicting declarations cannot silently win by list order.
                var conflict = providers.Any(x => !Equivalent(first, x));
                return new Requirement(group.Key, first, conflict);
            }).ToArray();

    private static bool Equivalent(OAuthProviderMetadata? left, OAuthProviderMetadata? right) =>
        left is null ? right is null : right is not null &&
        string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
        string.Equals(left.AuthorizationEndpoint, right.AuthorizationEndpoint, StringComparison.Ordinal) &&
        string.Equals(left.TokenEndpoint, right.TokenEndpoint, StringComparison.Ordinal) &&
        string.Equals(left.RevocationEndpoint, right.RevocationEndpoint, StringComparison.Ordinal) &&
        string.Equals(left.ClientAuthentication, right.ClientAuthentication, StringComparison.Ordinal) &&
        left.AuthorizationParameters.Count == right.AuthorizationParameters.Count &&
        left.AuthorizationParameters.All(x => right.AuthorizationParameters.TryGetValue(x.Key, out var value) &&
            string.Equals(x.Value, value, StringComparison.Ordinal));
}
