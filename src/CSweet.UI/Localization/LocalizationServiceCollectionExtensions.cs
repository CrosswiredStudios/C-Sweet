using CSweet.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.JSInterop;
using MudBlazor;

namespace CSweet.UI.Localization;

public static class LocalizationServiceCollectionExtensions
{
    /// <summary>Registers the culture state, localizer, time formatting, and MudBlazor string bridge.</summary>
    public static IServiceCollection AddCSweetLocalization(this IServiceCollection services)
    {
        services.AddSingleton<LocalizationResources>();
        services.TryAddScoped<AuthSessionStore>();
        services.AddScoped(sp => new AppCulture(sp.GetService<IJSRuntime>(), sp.GetRequiredService<AuthSessionStore>()));
        services.AddScoped<IAppLocalizer, AppLocalizer>();
        services.AddScoped<UserTime>();
        services.AddTransient<MudLocalizer, CSweetMudLocalizer>();
        return services;
    }
}
