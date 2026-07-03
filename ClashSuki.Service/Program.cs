using ClashSuki.Service;
using ClashSuki.ServiceContract;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    ServiceDiagnostics.Write(
        "未处理异常",
        (e.ExceptionObject as Exception)?.ToString() ?? "没有可用的异常信息。",
        "FATAL");
};

var commandLineExitCode = ServiceCommandLine.TryExecute(args);
if (commandLineExitCode is not null)
{
    return commandLineExitCode.Value;
}

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
            services.AddSingleton<NamedPipeClientAuthorizer>();
            services.AddSingleton<ServiceCommandDispatcher>();
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
