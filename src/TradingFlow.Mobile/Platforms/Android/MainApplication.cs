using Android.App;
using Android.Runtime;

// Required for HapticFeedback. Without it the platform silently ignores every
// haptic request, so order confirmations would land with no tactile signal.
[assembly: UsesPermission(Android.Manifest.Permission.Vibrate)]

namespace TradingFlow.Mobile;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
