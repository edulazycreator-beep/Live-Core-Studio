using System.Windows.Threading;

namespace LiveCoreStudio.UI.Services;

/// <summary>Marshals work onto the UI thread. Engine events arrive on background threads.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}
