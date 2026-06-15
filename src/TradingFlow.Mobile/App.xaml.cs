using Microsoft.Extensions.DependencyInjection;

namespace TradingFlow.Mobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		AppServices.Api.NormalizeBackendUrlForDevice();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}
