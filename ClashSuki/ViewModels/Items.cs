using CommunityToolkit.Mvvm.ComponentModel;
using ClashSuki.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using Windows.UI.Text;

namespace ClashSuki.ViewModels;

public sealed partial class ProxyGroupItemViewModel : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string type = "";
    [ObservableProperty] private string currentNode = "-";
    [ObservableProperty] private string fixedNode = "";
    [ObservableProperty] private string icon = "";
    [ObservableProperty] private Uri? iconUri;
    [ObservableProperty] private int nodeCount;
    [ObservableProperty] private int? delay;
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool hidden;
    [ObservableProperty] private string testUrl = "https://www.gstatic.com/generate_204";
    [ObservableProperty] private int timeoutMs;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string sortMode = "default";
    [ObservableProperty] private bool sortDescending;

    public IList<NodeItemViewModel> Nodes { get; } = [];
    public string DelayText => Formatters.Delay(Delay);
    public Brush DelayForeground => Formatters.DelayBrush(Delay);
    public bool CanSwitch => Type is "Selector" or "URLTest" or "Fallback" or "LoadBalance" or "url-test" or "load-balance";
    public string NodeCountDisplay => $"{FilteredNodes.Count}/{NodeCount}";
    public bool IsGroupDelayRunning => Nodes.Any(n => n.IsGroupDelayPending);
    public string SubtitleText => $"{Type} · {CurrentNode}";

    public ObservableCollection<NodeItemViewModel> FilteredNodes { get; } = [];

    partial void OnCurrentNodeChanged(string value) => OnPropertyChanged(nameof(SubtitleText));

    partial void OnDelayChanged(int? value)
    {
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(DelayForeground));
    }

    partial void OnSearchTextChanged(string value) => RefreshFiltered();
    partial void OnSortModeChanged(string value) => RefreshFiltered();
    partial void OnSortDescendingChanged(bool value) => RefreshFiltered();
    partial void OnNodeCountChanged(int value) => OnPropertyChanged(nameof(NodeCountDisplay));
    partial void OnFixedNodeChanged(string value) => UpdateFixedFlags();

    public void RefreshFiltered()
    {
        CollectionSync.Sync(FilteredNodes, BuildFilteredList());
        OnPropertyChanged(nameof(NodeCountDisplay));
    }

    public void NotifyGroupDelayState() => OnPropertyChanged(nameof(IsGroupDelayRunning));

    private void UpdateFixedFlags()
    {
        foreach (var node in Nodes)
        {
            node.IsFixed = !string.IsNullOrWhiteSpace(FixedNode)
                           && node.Name.Equals(FixedNode, StringComparison.OrdinalIgnoreCase);
        }
    }

    private List<NodeItemViewModel> BuildFilteredList()
    {
        IEnumerable<NodeItemViewModel> result = Nodes;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var query = SearchText.Trim();
            if (!Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Where(n => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
            }
        }

        var list = SortMode switch
        {
            "delay" => result.OrderBy(n => n, Comparer<NodeItemViewModel>.Create(CompareDelay)).ToList(),
            "name" => result.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => result.ToList()
        };

        if (SortDescending)
        {
            list.Reverse();
        }

        return list;
    }

    private static int CompareDelay(NodeItemViewModel a, NodeItemViewModel b)
    {
        static int Rank(int? delay) => delay switch
        {
            null => 1,
            <= 0 => 2,
            _ => 0
        };

        var rank = Rank(a.Delay).CompareTo(Rank(b.Delay));
        if (rank != 0)
        {
            return rank;
        }

        return (a.Delay ?? int.MaxValue).CompareTo(b.Delay ?? int.MaxValue);
    }
}

public sealed partial class NodeItemViewModel : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string groupName = "";
    [ObservableProperty] private string proxyType = "";
    [ObservableProperty] private int? delay;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isFixed;
    [ObservableProperty] private bool isNestedGroup;
    [ObservableProperty] private bool isTesting;
    [ObservableProperty] private bool isGroupDelayPending;

    public string DelayText => Formatters.Delay(Delay);
    public string DelayButtonText => Formatters.DelayButton(Delay);
    public Brush DelayForeground => Formatters.DelayBrush(Delay);
    public bool IsDelayLoading => IsTesting || IsGroupDelayPending;
    public bool ShowProxyType => !string.IsNullOrWhiteSpace(ProxyType) && !IsNestedGroup;
    public FontWeight NameFontWeight => new((ushort)(IsSelected ? 600 : 400));
    public Brush CardBorderBrush => IsFixed
        ? DelayBrushes.Warning
        : IsSelected
            ? DelayBrushes.Primary
            : DelayBrushes.Transparent;
    public Thickness CardBorderThickness => IsSelected || IsFixed ? new(2, 0, 2, 0) : new(0);
    public Brush CardBackgroundBrush => IsFixed
        ? DelayBrushes.FixedBackground
        : IsSelected
            ? DelayBrushes.SelectedBackground
            : DelayBrushes.NeutralBackground;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(NameFontWeight));
        OnPropertyChanged(nameof(CardBorderBrush));
        OnPropertyChanged(nameof(CardBorderThickness));
        OnPropertyChanged(nameof(CardBackgroundBrush));
    }

    partial void OnIsFixedChanged(bool value)
    {
        OnPropertyChanged(nameof(CardBorderBrush));
        OnPropertyChanged(nameof(CardBorderThickness));
        OnPropertyChanged(nameof(CardBackgroundBrush));
    }

    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(IsDelayLoading));
    partial void OnIsGroupDelayPendingChanged(bool value) => OnPropertyChanged(nameof(IsDelayLoading));
    partial void OnDelayChanged(int? value)
    {
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(DelayButtonText));
        OnPropertyChanged(nameof(DelayForeground));
    }

    partial void OnProxyTypeChanged(string value) => OnPropertyChanged(nameof(ShowProxyType));
    partial void OnIsNestedGroupChanged(bool value) => OnPropertyChanged(nameof(ShowProxyType));
}

public sealed partial class ConnectionItemViewModel : ObservableObject
{
    [ObservableProperty] private string id = "";
    [ObservableProperty] private string host = "--";
    [ObservableProperty] private string port = "";
    [ObservableProperty] private string network = "--";
    [ObservableProperty] private string rule = "--";
    [ObservableProperty] private string rulePayload = "";
    [ObservableProperty] private string chain = "--";
    [ObservableProperty] private string processText = "";
    [ObservableProperty] private string processPath = "";
    [ObservableProperty] private Uri? processIconUri;
    [ObservableProperty] private string uploadText = "0 B";
    [ObservableProperty] private string downloadText = "0 B";
    [ObservableProperty] private string upSpeedText = "";
    [ObservableProperty] private string downSpeedText = "";
    [ObservableProperty] private string startText = "";
    [ObservableProperty] private bool isClosed;
    [ObservableProperty] private bool isClosing;
    [ObservableProperty] private long uploadBytes;
    [ObservableProperty] private long downloadBytes;
    [ObservableProperty] private DateTimeOffset startTime = DateTimeOffset.MinValue;
    [ObservableProperty] private DateTimeOffset lastSeenAt = DateTimeOffset.MinValue;
    [ObservableProperty] private DateTimeOffset closedAt = DateTimeOffset.MinValue;

    public string HostDisplay => string.IsNullOrWhiteSpace(Port) ? Host : $"{Host}:{Port}";
    public string SubtitleText => string.Join("  ", new[] { ProcessText, StartText }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string RuleDisplay => string.IsNullOrWhiteSpace(RulePayload) ? Rule : $"{Rule} · {RulePayload}";
    public string TransferDisplay => $"{UploadText} / {DownloadText}";
    public string SpeedDisplay => string.Join("  ", new[] { UpSpeedText, DownSpeedText }.Where(s => !string.IsNullOrEmpty(s)));
    public string SearchText => $"{Host} {Port} {Network} {Rule} {RulePayload} {Chain} {ProcessText} {Id}";

    partial void OnPortChanged(string value) => OnPropertyChanged(nameof(HostDisplay));
    partial void OnHostChanged(string value) => OnPropertyChanged(nameof(HostDisplay));
    partial void OnProcessTextChanged(string value) => OnPropertyChanged(nameof(SubtitleText));
    partial void OnStartTextChanged(string value) => OnPropertyChanged(nameof(SubtitleText));
    partial void OnRuleChanged(string value) => OnPropertyChanged(nameof(RuleDisplay));
    partial void OnRulePayloadChanged(string value) => OnPropertyChanged(nameof(RuleDisplay));
    partial void OnUploadTextChanged(string value) => OnPropertyChanged(nameof(TransferDisplay));
    partial void OnDownloadTextChanged(string value) => OnPropertyChanged(nameof(TransferDisplay));
    partial void OnUpSpeedTextChanged(string value) => OnPropertyChanged(nameof(SpeedDisplay));
    partial void OnDownSpeedTextChanged(string value) => OnPropertyChanged(nameof(SpeedDisplay));
}

public sealed class LogItemViewModel
{
    public string Source { get; init; } = "应用";
    public string Level { get; init; } = "INFO";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
    public string Message { get; init; } = "";
    public string Details { get; init; } = "";
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public string DisplayText => HasDetails
        ? $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] [{Source}] {Message}{Environment.NewLine}{Details}"
        : $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] [{Source}] {Message}";
}

public sealed partial class ProfileItemViewModel : ObservableObject
{
    [ObservableProperty] private string uid = "";
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string type = "local";
    [ObservableProperty] private string url = "";
    [ObservableProperty] private string file = "";
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string updatedText = "未更新";
    [ObservableProperty] private string usedText = "";
    [ObservableProperty] private string expireText = "";
    [ObservableProperty] private string userAgent = "";
    [ObservableProperty] private string authToken = "";
    [ObservableProperty] private string ageSecretKey = "";
    [ObservableProperty] private int? intervalMinutes;
    [ObservableProperty] private bool autoUpdate;

    public string StatusText => IsBusy ? "处理中" : TypeLabel;
    public string TypeLabel => string.Equals(Type, "remote", StringComparison.OrdinalIgnoreCase) ? "远程" : "本地";
    public bool IsRemote => string.Equals(Type, "remote", StringComparison.OrdinalIgnoreCase);
    public bool IsLocal => !IsRemote;
    public string SourceText => string.IsNullOrWhiteSpace(Url) ? File : Url;
    public string DetailText => string.Join("  ", new[] { UpdatedText, UsedText, ExpireText }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string UpdatePolicyText => AutoUpdate
        ? $"自动更新 · {((IntervalMinutes is > 0 ? IntervalMinutes.Value : 1440) / 60.0):0.#} 小时"
        : "手动更新";
    public Windows.UI.Color StatusColor => IsBusy ? Windows.UI.Color.FromArgb(255, 255, 165, 0) : IsActive ? Windows.UI.Color.FromArgb(255, 16, 137, 62) : Windows.UI.Color.FromArgb(255, 128, 128, 128);

    partial void OnIsActiveChanged(bool value) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusColor)); }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusColor)); }
    partial void OnTypeChanged(string value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(IsRemote));
        OnPropertyChanged(nameof(IsLocal));
    }
    partial void OnUrlChanged(string value) => OnPropertyChanged(nameof(SourceText));
    partial void OnFileChanged(string value) => OnPropertyChanged(nameof(SourceText));
    partial void OnUpdatedTextChanged(string value) => OnPropertyChanged(nameof(DetailText));
    partial void OnUsedTextChanged(string value) => OnPropertyChanged(nameof(DetailText));
    partial void OnExpireTextChanged(string value) => OnPropertyChanged(nameof(DetailText));
    partial void OnIntervalMinutesChanged(int? value) => OnPropertyChanged(nameof(UpdatePolicyText));
    partial void OnAutoUpdateChanged(bool value) => OnPropertyChanged(nameof(UpdatePolicyText));
}

public sealed partial class WebUiPanelViewModel : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string url = "";
}

public sealed partial class ProviderItemViewModel : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string type = "";
    [ObservableProperty] private string updatedText = "";
    [ObservableProperty] private string usedText = "";
    [ObservableProperty] private string expireText = "";
    [ObservableProperty] private string behavior = "";
    [ObservableProperty] private int ruleCount;

    public string RuleCountText => RuleCount > 0 ? $"{RuleCount} 条" : "";
    partial void OnRuleCountChanged(int value) => OnPropertyChanged(nameof(RuleCountText));
}

public sealed partial class RuleProviderItemViewModel : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string type = "";
    [ObservableProperty] private string vehicleType = "";
    [ObservableProperty] private string behavior = "";
    [ObservableProperty] private string format = "";
    [ObservableProperty] private string updatedText = "未更新";
    [ObservableProperty] private int ruleCount;
    [ObservableProperty] private bool isUpdating;
    [ObservableProperty] private bool isViewing;
    [ObservableProperty] private string path = "";
    [ObservableProperty] private string payload = "";

    public string RuleCountText => RuleCount > 0 ? $"{RuleCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} 条" : "";
    public string FormatText => string.IsNullOrWhiteSpace(Format) ? "YamlRule" : Format;
    public string ProviderKindText => string.Join("::", new[] { VehicleType, Behavior }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string DetailText => string.Join(" · ", new[] { UpdatedText, ProviderKindText, RuleCountText }.Where(s => !string.IsNullOrWhiteSpace(s)));

    partial void OnRuleCountChanged(int value)
    {
        OnPropertyChanged(nameof(RuleCountText));
        OnPropertyChanged(nameof(DetailText));
    }
    partial void OnFormatChanged(string value)
    {
        OnPropertyChanged(nameof(FormatText));
        OnPropertyChanged(nameof(DetailText));
    }

    partial void OnVehicleTypeChanged(string value)
    {
        OnPropertyChanged(nameof(ProviderKindText));
        OnPropertyChanged(nameof(DetailText));
    }

    partial void OnBehaviorChanged(string value)
    {
        OnPropertyChanged(nameof(ProviderKindText));
        OnPropertyChanged(nameof(DetailText));
    }

    partial void OnUpdatedTextChanged(string value) => OnPropertyChanged(nameof(DetailText));
}
