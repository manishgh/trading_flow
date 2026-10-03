using Microsoft.Extensions.DependencyInjection;

namespace TradingFlow.Mobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
        var window=new Window(new AppShell());
        window.Stopped+=(_,_)=>Services.PreparationNotifications.Stop();
        window.Resumed+=(_,_)=>Services.PreparationNotifications.Start();
        window.Destroying+=(_,_)=>Services.PreparationNotifications.Stop();
        return window;
	}
}
