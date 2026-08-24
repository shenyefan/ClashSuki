namespace ClashSuki.Services;

public sealed partial class AppCoordinator
{
    public async Task RepairServiceAsync()
    {
        try
        {
            await _serviceManager.RepairAsync(_cts.Token);
            await _dispatcher.RunAsync(() =>
            {
                Runtime.TunServiceStatusText = "正在退出并修复服务";
                Runtime.IsTunToggleAvailable = false;
                Logs.AddApp("INFO", "已启动服务修复程序，应用即将退出", LogSources.Service);
            });
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.TunServiceStatusText = "服务修复失败";
                Runtime.IsTunToggleAvailable = false;
                Runtime.ShowTunServiceRepair = true;
            });
            throw;
        }
    }

    public async Task InstallPortableServiceAsync()
    {
        try
        {
            await _serviceManager.InstallPortableServiceAsync(_cts.Token);
            var status = await _serviceManager.GetStatusAsync(_cts.Token);
            await _dispatcher.RunAsync(() =>
            {
                Runtime.ApplyTunCapability(status);
                Logs.AddApp("INFO", "便携服务安装完成", LogSources.Service);
            });
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            var status = await _serviceManager.GetStatusAsync(CancellationToken.None);
            await _dispatcher.RunAsync(() => Runtime.ApplyTunCapability(status));
            throw;
        }
    }

    public async Task StopServiceAsync()
    {
        if (await YamlConfigService.IsTunEnabledAsync(AppPaths.RuntimeConfigPath, _cts.Token))
        {
            await SetTunAsync(false);
        }

        await _serviceManager.StopHostAsync(_cts.Token);
        var status = await _serviceManager.GetStatusAsync(_cts.Token);
        await _dispatcher.RunAsync(() =>
        {
            Runtime.ApplyTunCapability(status);
            Logs.AddApp("INFO", "ClashSuki 服务已停止，将在启用虚拟网卡时按需启动", LogSources.Service);
        });
        await RefreshRuntimeAsync(_cts.Token);
    }
}
