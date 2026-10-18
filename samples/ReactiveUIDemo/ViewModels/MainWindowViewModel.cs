using ReactiveUI.Reactive;

namespace ReactiveUIDemo.ViewModels
{
    internal class MainWindowViewModel : ReactiveObject
    {
        public RoutedViewHostPageViewModel RoutedViewHost { get; } = new();
    }
}
