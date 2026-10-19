using CSweet.Contracts.Localization;

namespace CSweet.Application.Localization;

/// <summary>
/// The platform instruction that makes agents write for the user in the workspace language while keeping
/// machine-read output stable. Appended by Headquarters to every agent inference request; agent packages
/// do not need to know about it.
/// </summary>
public static class AgentLanguageInstructions
{
    /// <summary>Returns null for English so existing prompts are byte-for-byte unchanged by default.</summary>
    public static string? For(string? cultureCode)
    {
        var normalized = SupportedCultures.Normalize(cultureCode);
        if (normalized is null || string.Equals(normalized, SupportedCultures.Default, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var culture = SupportedCultures.Find(normalized);
        return $"""
            Workspace language: {culture.EnglishName} ({culture.NativeName}, {culture.Code}).
            Write everything a person will read in {culture.EnglishName}: chat replies, questions, status updates,
            plans, summaries, review notes, incident reports, deliverable descriptions, and document prose.
            Keep machine-read output exactly as your instructions specify, in English: JSON keys and enum-like
            string values, identifiers, tool and capability names, file and branch names, commit messages, code,
            commands, log excerpts, and quoted error text. Translate the user's intent, never the data.
            If a person writes to you in a different language than the workspace language, answer in the
            language they used. Keep product and proper names (C-Sweet, GitHub, Discord, model names) unchanged.
            """;
    }
}
