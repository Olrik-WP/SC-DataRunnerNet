namespace DataRunner.App.Services;

public interface INavigationService
{
    void NavigateTo<TView>() where TView : class;
    void NavigateTo(Type viewType);

    /// <summary>Raised every time the active view changes.</summary>
    event EventHandler<Type>? Navigated;
}

public sealed class NavigationService : INavigationService
{
    public event EventHandler<Type>? Navigated;

    public Type? CurrentView { get; private set; }

    public void NavigateTo<TView>() where TView : class
        => NavigateTo(typeof(TView));

    public void NavigateTo(Type viewType)
    {
        CurrentView = viewType;
        Navigated?.Invoke(this, viewType);
    }
}
