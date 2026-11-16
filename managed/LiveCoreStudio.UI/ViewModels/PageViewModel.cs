using CommunityToolkit.Mvvm.ComponentModel;

namespace LiveCoreStudio.UI.ViewModels;

/// <summary>Base for every page shown in the shell's content area.</summary>
public abstract class PageViewModel(string key, string title, string iconKey) : ObservableObject
{
    /// <summary>Stable identifier, persisted as the last opened page.</summary>
    public string Key { get; } = key;
    public string Title { get; } = title;
    /// <summary>Resource key of the navigation glyph.</summary>
    public string IconKey { get; } = iconKey;

    /// <summary>Called when the page becomes visible.</summary>
    public virtual void OnNavigatedTo() { }
}
