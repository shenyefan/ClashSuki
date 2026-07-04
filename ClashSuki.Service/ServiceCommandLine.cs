namespace ClashSuki.Service;

internal static class ServiceCommandLine
{
    public static int? TryExecute(string[] arguments)
    {
        return arguments switch
        {
            ["--replace-core", var sourcePath, var destinationPath] => Execute(
                "替换内核",
                () => CoreReplacer.Replace(sourcePath, destinationPath),
                "内核替换成功"),
            [] => null,
            _ => FailUnknown(arguments)
        };
    }

    private static int Execute(string operation, Action action, string successMessage)
    {
        try
        {
            ServiceDiagnostics.Write(operation, "开始执行");
            action();
            ServiceDiagnostics.Write(operation, successMessage);
            return 0;
        }
        catch (Exception ex)
        {
            ServiceDiagnostics.WriteException(operation, "执行失败", ex);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int FailUnknown(IEnumerable<string> arguments)
    {
        var command = string.Join(' ', arguments);
        ServiceDiagnostics.Write("命令行", $"不支持的参数: {command}", "ERROR");
        Console.Error.WriteLine($"不支持的参数: {command}");
        return 2;
    }
}
