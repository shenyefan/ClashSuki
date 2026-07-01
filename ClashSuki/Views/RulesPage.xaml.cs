using ClashSuki.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSuki.Views;

public sealed partial class RulesPage : Page
{
    public RulesPage()
    {
        PageBinding.Bind(this, vm => vm.RulesVm);
        InitializeComponent();
    }

    private void RuleSwitch_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle)
        {
            toggle.Tag = "ready";
        }
    }

    private async void RuleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: "ready" } toggle ||
            toggle.DataContext is not RuleItemViewModel rule ||
            DataContext is not RulesViewModel viewModel ||
            rule.IsSyncing ||
            rule.IsUpdating)
        {
            return;
        }

        await viewModel.SetRuleEnabledAsync(rule, toggle.IsOn);
    }
}
