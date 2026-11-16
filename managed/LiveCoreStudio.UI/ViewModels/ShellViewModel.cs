using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LiveCoreStudio.UI.ViewModels;

/// <summary>Main window: navigation between pages plus the always-visible engine status.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Action<string> _rememberPage;

    public ShellViewModel(IReadOnlyList<PageViewModel> pages, EngineStatusViewModel engine, string startPage, Action<string> rememberPage)
    {
        Pages = pages;
        Engine = engine;
        _rememberPage = rememberPage;
        _currentPage = pages.FirstOrDefault(p => p.Key == startPage) ?? pages[0];
        _currentPage.OnNavigatedTo();
    }

    public IReadOnlyList<PageViewModel> Pages { get; }
    public EngineStatusViewModel Engine { get; }

    [ObservableProperty] private PageViewModel _currentPage;

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        value.OnNavigatedTo();
        _rememberPage(value.Key);
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => CurrentPage = page;
}
