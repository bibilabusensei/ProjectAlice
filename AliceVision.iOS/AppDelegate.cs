using Foundation;
using UIKit;

namespace AliceVision.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) => true;

    public override UISceneConfiguration GetConfiguration(
        UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options) =>
        new UISceneConfiguration("Default Configuration", connectingSceneSession.Role)
        {
            DelegateType = typeof(WindowSceneDelegate)
        };
}
