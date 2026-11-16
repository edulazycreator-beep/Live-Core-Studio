using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using LiveCoreStudio.UI.ViewModels;

namespace LiveCoreStudio.UI.Views;

public partial class DiagnosticsView : UserControl
{
    private INotifyCollectionChanged? _source;

    public DiagnosticsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (e.NewValue is DiagnosticsViewModel vm)
        {
            _source = vm.Entries;
            _source.CollectionChanged += OnEntriesChanged;
        }
    }

    private void Detach()
    {
        if (_source is not null) _source.CollectionChanged -= OnEntriesChanged;
        _source = null;
    }

    // Follow the tail, like a console.
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
