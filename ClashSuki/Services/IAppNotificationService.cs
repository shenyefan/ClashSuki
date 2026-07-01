using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSuki.Services;

public interface IAppNotificationService
{
    void Success(string message, string title = "操作成功", string source = LogSources.Application);

    void Info(string message, string title = "提示", string source = LogSources.Application);

    void Warning(
        string message,
        string title = "注意",
        string source = LogSources.Application,
        Exception? exception = null);

    void Error(
        string message,
        string title = "操作失败",
        string source = LogSources.Application,
        Exception? exception = null);

    Task<bool> ConfirmAsync(
        XamlRoot xamlRoot,
        string action,
        string message,
        string primaryButtonText = "确认",
        string closeButtonText = "取消",
        string source = LogSources.Application);

    Task<ContentDialogResult> ShowDialogAsync(
        XamlRoot xamlRoot,
        ContentDialog dialog,
        string action,
        string source = LogSources.Application);
}
