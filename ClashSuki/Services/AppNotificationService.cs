using ClashSuki.Stores;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSuki.Services;

public sealed class AppNotificationService : IAppNotificationService
{
    private readonly RuntimeStore _runtime;
    private readonly DispatcherQueue _dispatcher;

    public AppNotificationService(RuntimeStore runtime, DispatcherQueue dispatcher)
    {
        _runtime = runtime;
        _dispatcher = dispatcher;
    }

    public void Success(
        string message,
        string title = "操作成功",
        string source = LogSources.Application,
        bool writeLog = true) =>
        Publish(title, message, source, "INFO", InfoBarSeverity.Success, writeLog);

    public void Info(
        string message,
        string title = "提示",
        string source = LogSources.Application,
        bool writeLog = true) =>
        Publish(title, message, source, "INFO", InfoBarSeverity.Informational, writeLog);

    public void Warning(
        string message,
        string title = "注意",
        string source = LogSources.Application,
        Exception? exception = null,
        bool writeLog = true)
    {
        if (writeLog)
        {
            var context = FormatMessage(title, message);
            if (exception is null)
            {
                DiagnosticLog.WriteApp(source, "WARN", context);
            }
            else
            {
                DiagnosticLog.WriteAppException(source, exception, context, "WARN");
            }
        }

        Enqueue(title, BuildDisplayMessage(message, exception), InfoBarSeverity.Warning);
    }

    public void Error(
        string message,
        string title = "操作失败",
        string source = LogSources.Application,
        Exception? exception = null)
    {
        var context = FormatMessage(title, message);
        if (exception is null)
        {
            DiagnosticLog.WriteApp(source, "ERROR", context);
        }
        else
        {
            DiagnosticLog.WriteAppException(source, exception, context);
        }

        Enqueue(title, BuildDisplayMessage(message, exception), InfoBarSeverity.Error);
    }

    public async Task<bool> ConfirmAsync(
        XamlRoot xamlRoot,
        string action,
        string message,
        string primaryButtonText = "确认",
        string closeButtonText = "取消",
        string source = LogSources.Application)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = action,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.WrapWholeWords
                },
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = closeButtonText,
                DefaultButton = ContentDialogButton.Close
            };

            return await ShowDialogAsync(xamlRoot, dialog, action, source) ==
                   ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            Error("无法显示确认对话框", $"{action}失败", source, ex);
            return false;
        }
    }

    public async Task<ContentDialogResult> ShowDialogAsync(
        XamlRoot xamlRoot,
        ContentDialog dialog,
        string action,
        string source = LogSources.Application)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(dialog);

        try
        {
            dialog.XamlRoot = xamlRoot;
            if (xamlRoot.Content is FrameworkElement themeRoot)
            {
                dialog.RequestedTheme = themeRoot.ActualTheme;
            }

            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Error("无法显示对话框", $"{action}失败", source, ex);
            return ContentDialogResult.None;
        }
    }

    private void Publish(
        string title,
        string message,
        string source,
        string level,
        InfoBarSeverity severity,
        bool writeLog = true)
    {
        if (writeLog)
        {
            DiagnosticLog.WriteApp(source, level, FormatMessage(title, message));
        }

        Enqueue(title, message, severity);
    }

    private void Enqueue(string title, string message, InfoBarSeverity severity)
    {
        if (_dispatcher.HasThreadAccess)
        {
            _runtime.PublishNotification(title, message, severity);
            return;
        }

        if (!_dispatcher.TryEnqueue(() => _runtime.PublishNotification(title, message, severity)))
        {
            DiagnosticLog.WriteApp(
                LogSources.UserInterface,
                "WARN",
                $"显示全局提示失败，界面调度队列已关闭，标题: {Normalize(title)}");
        }
    }

    private static string FormatMessage(string title, string message)
    {
        var normalizedTitle = Normalize(title);
        var normalizedMessage = Normalize(message);
        return normalizedTitle is "操作成功" or "提示" or "注意" or "操作失败"
            ? normalizedMessage
            : $"{normalizedTitle}：{normalizedMessage}";
    }

    private static string Normalize(string value) =>
        value.ReplaceLineEndings(" ").Trim();

    private static string BuildDisplayMessage(string message, Exception? exception)
    {
        var normalizedMessage = Normalize(message);
        if (exception is null || string.IsNullOrWhiteSpace(exception.Message))
        {
            return normalizedMessage;
        }

        return $"{normalizedMessage}：{Normalize(exception.Message)}";
    }
}
