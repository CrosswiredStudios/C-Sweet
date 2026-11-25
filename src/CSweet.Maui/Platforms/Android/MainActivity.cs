using Android.App;
using Android.Content.PM;
using Android.OS;

namespace CSweet.Maui;

[IntentFilter([Android.Content.Intent.ActionView], Categories = [Android.Content.Intent.CategoryDefault, Android.Content.Intent.CategoryBrowsable],
    DataScheme = "https", DataHost = "account.c-sweet.com", DataPath = "/office/link", AutoVerify = true)]
[Activity(Exported = true, LaunchMode = LaunchMode.SingleTop, Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (savedInstanceState == null) OfficeLinkActivation.Accept(Intent?.DataString);
    }

    protected override void OnNewIntent(Android.Content.Intent? intent)
    {
        base.OnNewIntent(intent);
        OfficeLinkActivation.Accept(intent?.DataString);
    }
}
