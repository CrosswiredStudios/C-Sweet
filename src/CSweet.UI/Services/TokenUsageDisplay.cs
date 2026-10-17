namespace CSweet.UI.Services;

public static class TokenUsageDisplay
{
    public static string Format(long value, long calls, long reported, long pending, bool unlinked = false)
    {
        if (calls == 0) return unlinked ? "Unknown — no linked calls" : "No calls";
        if (reported == 0) return pending == calls ? "Pending" : pending > 0 ? "Pending / unknown" : "Unknown";
        var amount = value.ToString("N0");
        return reported == calls ? amount : $"{amount} reported · incomplete";
    }
}
