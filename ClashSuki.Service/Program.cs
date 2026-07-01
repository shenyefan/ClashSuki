using ClashSuki.Service;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    ServiceDiagnostics.Write(
        "未处理异常",
        (e.ExceptionObject as Exception)?.ToString() ?? "没有可用的异常信息。",
        "FATAL");
};

if (args is ["--install-service"])
{
    try
    {
        ServiceDiagnostics.Write("安装服务", "开始执行服务安装。");
        ServiceInstaller.Install();
        ServiceDiagnostics.Write("安装服务", "服务安装成功。");
        return 0;
    }
    catch (Exception ex)
    {
        ServiceDiagnostics.Write("安装服务", ex.ToString(), "ERROR");
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args is ["--uninstall-service"])
{
    try
    {
        ServiceDiagnostics.Write("卸载服务", "开始执行服务卸载。");
        ServiceInstaller.Uninstall();
        ServiceDiagnostics.Write("卸载服务", "服务卸载成功。");
        return 0;
    }
    catch (Exception ex)
    {
        ServiceDiagnostics.Write("卸载服务", ex.ToString(), "ERROR");
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args is ["--replace-core", var sourcePath, var destinationPath])
{
    try
    {
        ServiceDiagnostics.Write("替换内核", $"开始替换内核；源文件={sourcePath}；目标文件={destinationPath}");
        CoreReplacer.Replace(sourcePath, destinationPath);
        ServiceDiagnostics.Write("替换内核", "内核替换成功。");
        return 0;
    }
    catch (Exception ex)
    {
        ServiceDiagnostics.Write("替换内核", ex.ToString(), "ERROR");
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

try
{
    var builder = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = ServiceInstaller.ServiceName;
        })
        .ConfigureLogging(logging =>
        {
            logging.AddEventLog(settings =>
            {
                settings.SourceName = ServiceInstaller.ServiceName;
            });
        })
        .ConfigureServices(services =>
        {
            services.AddHostedService<Worker>();
        });

    await builder.Build().RunAsync();
    return 0;
}
catch (Exception ex)
{
    ServiceDiagnostics.Write("服务宿主", ex.ToString(), "FATAL");
    return 1;
}
