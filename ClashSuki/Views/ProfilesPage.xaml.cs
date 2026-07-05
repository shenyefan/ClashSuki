using ClashSuki.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Text;
using Windows.Storage.Pickers;
using WinRT.Interop;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;

namespace ClashSuki.Views;

public sealed partial class ProfilesPage : Page
{
    private ProfileItemViewModel? _profileFileEditingProfile;

    public ProfilesPage()
    {
        PageBinding.Bind(this, vm => vm.ProfilesVm);
        InitializeComponent();
    }

    private async void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilesViewModel viewModel ||
            GetProfileFromSender(sender) is not { } profile)
        {
            return;
        }

        viewModel.BeginEditProfile(profile);
        await viewModel.Runtime.Notifications.ShowDialogAsync(
            XamlRoot,
            EditProfileDialog,
            "编辑订阅",
            LogSources.Subscription);
    }

    private async void UpdateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfilesViewModel viewModel &&
            GetProfileFromSender(sender) is { } profile)
        {
            await viewModel.UpdateProfileCommand.ExecuteAsync(profile);
        }
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilesViewModel viewModel ||
            GetProfileFromSender(sender) is not { } profile)
        {
            return;
        }

        if (!await viewModel.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "删除订阅",
                $"确定删除订阅「{profile.Name}」吗？关联的配置文件也会被删除，此操作无法撤销。",
                "删除",
                "取消",
                LogSources.Subscription))
        {
            return;
        }

        await viewModel.DeleteProfileCommand.ExecuteAsync(profile);
    }

    private async void OpenProfileFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilesViewModel viewModel ||
            GetProfileFromSender(sender) is not { } profile)
        {
            return;
        }

        var content = await viewModel.ReadProfileFileAsync(profile);
        if (content is null)
        {
            return;
        }

        _profileFileEditingProfile = profile;
        ProfileFileDialog.XamlRoot = XamlRoot;
        ProfileFileDialog.Title = $"编辑配置文件 - {profile.Name}";
        ProfileFilePathText.Text = viewModel.GetProfileFilePath(profile);
        ProfileFileEditor.Document.SetText(TextSetOptions.None, content);
        await viewModel.Runtime.Notifications.ShowDialogAsync(
            XamlRoot,
            ProfileFileDialog,
            "编辑订阅配置文件",
            LogSources.Subscription);
    }

    private static ProfileItemViewModel? GetProfileFromSender(object sender) =>
        sender is FrameworkElement { Tag: ProfileItemViewModel tagged }
            ? tagged
            : sender is FrameworkElement { DataContext: ProfileItemViewModel profile }
                ? profile
                : null;

    private async void ProfileCard_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilesViewModel viewModel ||
            GetProfileFromSender(sender) is not { } profile ||
            profile.IsActive ||
            viewModel.IsSwitchingProfile)
        {
            return;
        }

        await viewModel.ActivateProfileWithRollbackAsync(profile);
    }

    private async void ImportLocalFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilesViewModel viewModel)
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".yaml");
        picker.FileTypeFilter.Add(".yml");

        if (App.CurrentWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
        }

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await viewModel.ImportLocalFileAsync(file.Path);
        }
    }

    private async void EditProfileDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (DataContext is not ProfilesViewModel viewModel)
        {
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await viewModel.SaveProfileEditAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void ProfileFileDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (DataContext is not ProfilesViewModel viewModel ||
            _profileFileEditingProfile is null)
        {
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            ProfileFileEditor.Document.GetText(TextGetOptions.None, out var content);
            if (content.EndsWith("\r", StringComparison.Ordinal))
            {
                content = content[..^1];
            }

            args.Cancel = !await viewModel.SaveProfileFileAsync(_profileFileEditingProfile, content);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
