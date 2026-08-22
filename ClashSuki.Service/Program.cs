using ClashSuki.Service;
using ClashSuki.ServiceContract;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    ServiceDiagnostics.WriteException(
        "未处理异常",
        "服务发生未处理异常",
        e.ExceptionObject as Exception ?? new InvalidOperationException("没有可用的异常信息"),
        "FATAL");
};

try
{
    var builder = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = ServiceProtocol.ServiceName;
        })
        .ConfigureLogging(logging =>
        {
            logging.AddEventLog(settings =>
            {
                settings.SourceName = ServiceProtocol.ServiceName;
            });
        })
        .ConfigureServices(services =>
        {
            services.AddSingleton<CoreProcessSupervisor>();
            services.AddSingleton<CoreLaunchRequestValidator>();
            services.AddSingleton<WindowsFirewallManager>();
            services.AddSingleton<LoopbackExemptionManager>();
            services.AddSingleton<NamedPipeClientAuthorizer>();
            services.AddSingleton<ServiceCommandDispatcher>();
            services.AddHostedService<Worker>();
        });

    await builder.Build().RunAsync();
    return 0;
}
catch (Exception ex)
{
    ServiceDiagnostics.WriteException("服务宿主", "服务宿主运行失败", ex, "FATAL");
    return 1;
}
