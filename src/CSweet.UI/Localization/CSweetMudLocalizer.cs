using Microsoft.Extensions.Localization;
using MudBlazor;

namespace CSweet.UI.Localization;

/// <summary>
/// Routes MudBlazor's built-in strings (data grid filters, pagers, pickers) through the app resources.
/// Keys live in the "Mud" area as <c>Mud.&lt;MudBlazorKey&gt;</c>; anything not provided keeps MudBlazor's English.
/// </summary>
public sealed class CSweetMudLocalizer(IAppLocalizer localizer) : MudLocalizer
{
    public override LocalizedString this[string key]
    {
        get
        {
            var resourceKey = "Mud." + key;
            return localizer.Has(resourceKey)
                ? new LocalizedString(key, localizer[resourceKey], resourceNotFound: false)
                : new LocalizedString(key, key, resourceNotFound: true);
        }
    }
}
