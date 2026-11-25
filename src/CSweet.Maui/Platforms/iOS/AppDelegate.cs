using Foundation;

namespace CSweet.Maui;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    public override bool ContinueUserActivity(UIKit.UIApplication application, NSUserActivity userActivity,
        UIKit.UIApplicationRestorationHandler completionHandler) =>
        OfficeLinkActivation.Accept(userActivity.WebPageUrl?.AbsoluteString) || base.ContinueUserActivity(application, userActivity, completionHandler);

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
