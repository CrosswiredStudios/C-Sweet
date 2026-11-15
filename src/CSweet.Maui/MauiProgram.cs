using CSweet.UI.Mobile;
using CSweet.UI.Services;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;

namespace CSweet.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // Headquarters pages reach their Headquarters through the phone tunnel (H4). Until a phone is connected the
        // enterprise router is not shown, so the API clients get a client with no address.
        builder.Services.AddScoped(_ => new HttpClient());
        builder.Services.AddCSweetApiClients();
        builder.Services.AddMudServices();

        // Sign in with C-Sweet and My Headquarters (H3).
        builder.Services.AddSingleton<IBrowserAuthenticator, MauiBrowserAuthenticator>();
        builder.Services.AddSingleton<ISecureValueStore, MauiSecureValueStore>();
        builder.Services.AddSingleton<HeadquartersTunnels>();
        builder.Services.AddSingleton<IHeadquartersConnector, MauiHeadquartersConnector>();
        builder.Services.AddCSweetMobileShell(AccountOptions());
        // The enterprise web app over the Headquarters session (TS-13 thin-shell prototype, Android).
        builder.Services.AddScoped<IEnterpriseAppHost, EnterpriseAppHost>(); // scoped: uses the localizer

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    private static MobileAccountOptions AccountOptions() =>
#if DEBUG
        // The local Aspire Market. On Android run `adb reverse tcp:5163 tcp:5163` so the phone's localhost reaches it.
        new() { Issuer = new Uri("http://localhost:5163/"), AllowHttpIssuer = true };
#else
        new();
#endif
}
