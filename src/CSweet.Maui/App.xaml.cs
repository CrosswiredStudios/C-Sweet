namespace CSweet.Maui;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "C-Sweet" };
		window.Activated += (_, _) => OfficeLinkActivation.Resume();
		return window;
	}
}
