using CSweet.UI.Localization;

namespace CSweet.UI.Mobile;

internal static class MobileText
{
    /// <summary>"Owner", plus a short id when another enterprise has the same name.</summary>
    public static string Detail(IAppLocalizer L, EnterpriseProfile profile)
    {
        var roleKey = $"Mobile.Role.{profile.MemberLabel}";
        var role = L.Has(roleKey) ? L[roleKey] : profile.MemberLabel;
        return profile.SecondLine is { } id ? $"{role} · {L.Format("Mobile.Chooser.Id", new { id })}" : role;
    }
}
