using ClashSuki.Service;

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
    var runtimeContext = ServiceRuntimeContext.Create(args);
    var builder = Host.CreateDefaultBuilder(Array.Empty<string>())
        .UseWindowsService(options =>
        {
            options.ServiceName = runtimeContext.ServiceName;
        })
        .ConfigureLogging(logging =>
        {
            logging.AddEventLog(settings =>
            {
                settings.SourceName = runtimeContext.ServiceName;
            });
        })
        .ConfigureServices(services =>
        {
            services.AddSingleton(runtimeContext);
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
