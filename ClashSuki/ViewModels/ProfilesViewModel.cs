using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;

namespace ClashSuki.ViewModels;

public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;

    [ObservableProperty] private string newProfileName = "";
    [ObservableProperty] private string newProfileUrl = "";
    [ObservableProperty] private string newUserAgent = "";
    [ObservableProperty] private string newAuthToken = "";
    [ObservableProperty] private string newAgeSecretKey = "";
    [ObservableProperty] private string newLocalName = "";
    [ObservableProperty] private string newLocalFileName = "";
    [ObservableProperty] private ProfileItemViewModel? editingProfile;
    [ObservableProperty] private string editName = "";
    [ObservableProperty] private string editUrl = "";
    [ObservableProperty] private string editUserAgent = "";
    [ObservableProperty] private string editAuthToken = "";
    [ObservableProperty] private string editAgeSecretKey = "";
    [ObservableProperty] private string editUpdateIntervalMinutes = "";
    [ObservableProperty] private bool editAutoUpdate;

    public ProfilesViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Profiles = coordinator.Profiles;
        Runtime = coordinator.Runtime;
        Profiles.Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ProfileCount));
    }

    public ProfileStore Profiles { get; }
    public RuntimeStore Runtime { get; }
    public int ProfileCount => Profiles.Items.Count;
    public bool IsEditingRemote => EditingProfile?.IsRemote == true;
    public bool IsEditingLocal => EditingProfile?.IsLocal == true;

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProfileUrl))
        {
            Runtime.Notifications.Warning(
                "订阅地址不能为空。",
                source: LogSources.Subscription);
            return;
        }

        if (await _coordinator.AddProfileAsync(
                NewProfileName,
                NewProfileUrl,
                NewUserAgent,
                NewAuthToken,
                NewAgeSecretKey,
                null))
        {
            NewProfileName = "";
            NewProfileUrl = "";
            NewUserAgent = "";
            NewAuthToken = "";
            NewAgeSecretKey = "";
        }
    }

    public void BeginEditProfile(ProfileItemViewModel profile)
    {
        EditingProfile = profile;
        EditName = profile.Name;
        EditUrl = profile.Url;
        EditUserAgent = profile.UserAgent;
        EditAuthToken = profile.AuthToken;
        EditAgeSecretKey = profile.AgeSecretKey;
        EditUpdateIntervalMinutes = profile.IntervalMinutes?.ToString() ?? "";
        EditAutoUpdate = profile.AutoUpdate;
    }

    public async Task<bool> SaveProfileEditAsync()
    {
        if (EditingProfile is null)
        {
            return false;
        }

        return await _coordinator.UpdateProfileSettingsAsync(
            EditingProfile.Uid,
            EditName,
            EditUrl,
            EditUserAgent,
            EditAuthToken,
            EditAgeSecretKey,
            null,
            ParsePositiveInt(EditUpdateIntervalMinutes),
            EditAutoUpdate);
    }

    public string GetProfileFilePath(ProfileItemViewModel profile) =>
        _coordinator.GetProfileFilePath(profile.Uid);

    public Task<string?> ReadProfileFileAsync(ProfileItemViewModel profile) =>
        _coordinator.ReadProfileFileAsync(profile.Uid);

    public Task<bool> SaveProfileFileAsync(ProfileItemViewModel profile, string content) =>
        _coordinator.SaveProfileFileAsync(profile.Uid, content);

    public async Task ImportLocalFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (await _coordinator.ImportLocalProfileFileAsync(NewLocalName, path))
        {
            NewLocalName = "";
            NewLocalFileName = "";
        }
    }

    [RelayCommand]
    private async Task CreateLocalProfileAsync()
    {
        var fileName = string.IsNullOrWhiteSpace(NewLocalFileName)
            ? $"{(string.IsNullOrWhiteSpace(NewLocalName) ? "local" : NewLocalName.Trim())}.yaml"
            : NewLocalFileName;

        if (await _coordinator.ImportLocalProfileAsync(
                string.IsNullOrWhiteSpace(NewLocalName) ? "本地配置" : NewLocalName,
                fileName,
                "proxies: []\nproxy-groups: []\nrules: []\n"))
        {
            NewLocalName = "";
            NewLocalFileName = "";
        }
    }

    [RelayCommand]
    private async Task UpdateProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile is not null)
            await _coordinator.UpdateProfileAsync(profile.Uid);
    }

    public async Task<bool> ActivateProfileWithRollbackAsync(ProfileItemViewModel profile)
    {
        if (profile.IsActive)
        {
            return true;
        }

        var previousUid = Profiles.ActiveUid;
        ApplyActiveVisualState(profile.Uid);

        var activated = await _coordinator.ActivateProfileAsync(profile.Uid);
        if (!activated)
        {
            ApplyActiveVisualState(previousUid);
        }

        return activated;
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile is not null)
            await _coordinator.DeleteProfileAsync(profile.Uid);
    }

    private static int? ParsePositiveInt(string value) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;

    private void ApplyActiveVisualState(string uid)
    {
        foreach (var item in Profiles.Items)
        {
            item.IsActive = string.Equals(item.Uid, uid, StringComparison.Ordinal);
        }
    }

    partial void OnEditingProfileChanged(ProfileItemViewModel? value)
    {
        OnPropertyChanged(nameof(IsEditingRemote));
        OnPropertyChanged(nameof(IsEditingLocal));
    }
}
