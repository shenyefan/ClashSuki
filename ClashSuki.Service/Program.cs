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
    var serviceName = ServiceRuntimeContext.GetServiceName(args);
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = Array.Empty<string>(),
        ContentRootPath = AppContext.BaseDirectory
    });
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = serviceName;
    });
    builder.Logging.AddEventLog(settings =>
    {
        settings.SourceName = serviceName;
    });
    builder.Services.AddSingleton(_ => ServiceRuntimeContext.Create(args));
    builder.Services.AddSingleton<CoreProcessSupervisor>();
    builder.Services.AddSingleton<CoreLaunchRequestValidator>();
    builder.Services.AddSingleton<WindowsFirewallManager>();
    builder.Services.AddSingleton<NamedPipeClientAuthorizer>();
    builder.Services.AddSingleton<ServiceCommandDispatcher>();
    builder.Services.AddHostedService<Worker>();

    await builder.Build().RunAsync();
    return 0;
}
catch (Exception ex)
{
    ServiceDiagnostics.WriteException("服务宿主", "服务宿主运行失败", ex, "FATAL");
    return 1;
}
