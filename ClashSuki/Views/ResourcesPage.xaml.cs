using System.ComponentModel;
using ClashSuki.Services;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ClashSuki.Views;

public sealed partial class ResourcesPage : Page
{
    private ResourcesViewModel? _viewModel;
    private bool _dialogShowing;

    public ResourcesPage()
    {
        PageBinding.Bind(this, vm => vm.Resources);
        InitializeComponent();
        Loaded += ResourcesPage_Loaded;
        Unloaded += ResourcesPage_Unloaded;
    }

    private void ResourcesPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_viewModel, DataContext))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        _viewModel = DataContext as ResourcesViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _ = _viewModel.LoadGeoDataCommand.ExecuteAsync(null);
            if (_viewModel.IsViewerOpen)
            {
                _ = ShowRuleProviderDialogAsync();
            }
        }
    }

    private void ResourcesPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel = null;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ResourcesViewModel.IsViewerOpen) || _viewModel is null)
        {
            return;
        }

        if (_viewModel.IsViewerOpen)
        {
            _ = ShowRuleProviderDialogAsync();
        }
        else if (_dialogShowing)
        {
            RuleProviderDialog.Hide();
        }
    }

    private async Task ShowRuleProviderDialogAsync()
    {
        if (_dialogShowing || _viewModel is null)
        {
            return;
        }

        _dialogShowing = true;
        try
        {
            await _viewModel.Runtime.Notifications.ShowDialogAsync(
                XamlRoot,
                RuleProviderDialog,
                "查看规则集合",
                LogSources.Resource);
        }
        finally
        {
            _dialogShowing = false;
        }
    }

    private async void RuleProviderDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_viewModel is not null)
        {
            await _viewModel.OpenViewerSourceCommand.ExecuteAsync(null);
        }
    }

    private void RuleProviderDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (_viewModel?.IsViewerOpen == true)
        {
            _viewModel.CloseViewerCommand.Execute(null);
        }
    }

    private void GeoModeDb_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.GeoDataMode = false;
        }
    }

    private void GeoModeDat_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.GeoDataMode = true;
        }
    }

    private async void GeoUrlTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await SaveGeoUrlIfDirtyAsync(sender);
    }

    private async void GeoUrlTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await SaveGeoUrlIfDirtyAsync(sender);
    }

    private async Task SaveGeoUrlIfDirtyAsync(object sender)
    {
        if (_viewModel is null || sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        switch (tag)
        {
            case "geoip" when _viewModel.IsGeoIpDirty:
                await _viewModel.SaveGeoIpCommand.ExecuteAsync(null);
                break;
            case "geosite" when _viewModel.IsGeoSiteDirty:
                await _viewModel.SaveGeoSiteCommand.ExecuteAsync(null);
                break;
            case "mmdb" when _viewModel.IsMmdbDirty:
                await _viewModel.SaveMmdbCommand.ExecuteAsync(null);
                break;
            case "asn" when _viewModel.IsAsnDirty:
                await _viewModel.SaveAsnCommand.ExecuteAsync(null);
                break;
        }
    }
}
