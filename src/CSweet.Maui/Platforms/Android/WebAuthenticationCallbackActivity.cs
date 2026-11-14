using Android.App;
using Android.Content;
using Android.Content.PM;

namespace CSweet.Maui;

/// <summary>Receives the C-Sweet sign-in redirect <c>com.csweet.app:/oauth/callback</c> and hands it to WebAuthenticator.</summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "com.csweet.app")]
public class WebAuthenticationCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
}
