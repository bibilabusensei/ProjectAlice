using Foundation;
using UIKit;

namespace AliceVision.iOS;

[Register("WindowSceneDelegate")]
public sealed class WindowSceneDelegate : UIWindowSceneDelegate
{
    public override UIWindow? Window { get; set; }

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is UIWindowScene windowScene)
        {
            Window = new UIWindow(windowScene)
            {
                RootViewController = new VisionViewController(),
                BackgroundColor = UIColor.Black
            };
            Window.MakeKeyAndVisible();
        }
    }
}
