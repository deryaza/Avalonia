using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ReactiveUI.Binding.Reactive;
using ReactiveUI.Avalonia.Reactive;
using ReactiveUIDemo.ViewModels;
using ReactiveUIDemo.Views;
using Splat;

namespace ReactiveUIDemo
{
    public class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            AppLocator.CurrentMutable.Register(() => new FooView(), typeof(IViewFor<FooViewModel>));
            AppLocator.CurrentMutable.Register(() => new BarView(), typeof(IViewFor<BarViewModel>));
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();
            base.OnFrameworkInitializationCompleted();
        }

        public static int Main(string[] args)
            => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        public static AppBuilder BuildAvaloniaApp()
          => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .UseReactiveUI(builder => { })
                .LogToTrace();
    }
}
