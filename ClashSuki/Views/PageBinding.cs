using CommunityToolkit.Mvvm.ComponentModel;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSuki.Views;

internal static class PageBinding
{
    public static void Bind(Page page)
    {
        Attach(page, vm => vm);
    }

    public static void Bind<TViewModel>(Page page, Func<MainViewModel, TViewModel> selector)
        where TViewModel : ObservableObject
    {
        Attach(page, selector);
    }

    /// <summary>必须在 InitializeComponent 之前调用，避免页面继承 MainViewModel 导致绑定错误。</summary>
    private static void Attach<TViewModel>(Page page, Func<MainViewModel, TViewModel> selector)
        where TViewModel : ObservableObject
    {
        Apply(page, selector);
        page.Loaded += (_, _) => Apply(page, selector);
    }

    private static void Apply<TViewModel>(Page page, Func<MainViewModel, TViewModel> selector)
        where TViewModel : ObservableObject
    {
        if (Application.Current is App { ViewModel: { } viewModel })
        {
            page.DataContext = selector(viewModel);
        }
    }
}
