using ClashSuki.Stores;

namespace ClashSuki.Services;

public sealed class RuntimeConfigService
{
    private readonly ProfileStore _profiles;
    private readonly OverrideRuntimeService _overrides;
    private readonly MihomoCoreManager _core;
    private readonly SemaphoreSlim _composeLock = new(1, 1);

    public RuntimeConfigService(
        ProfileStore profiles,
        OverrideRuntimeService overrides,
        MihomoCoreManager core)
    {
        _profiles = profiles;
        _overrides = overrides;
        _core = core;
    }

    public async Task<OverrideApplyResult> RebuildAsync(
        CancellationToken cancellationToken = default)
    {
        await _composeLock.WaitAsync(cancellationToken);
        try
        {
            var sourceYaml = await _profiles.BuildActiveRuntimeYamlAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(sourceYaml))
            {
                sourceYaml = await File.ReadAllTextAsync(AppPaths.BaseConfigPath, cancellationToken);
            }

            Directory.CreateDirectory(AppPaths.ConfigDirectory);
            var tempPath = Path.Combine(
                AppPaths.ConfigDirectory,
                $"mihomo.compose.{Guid.NewGuid():N}.tmp.yaml");
            try
            {
                await File.WriteAllTextAsync(tempPath, sourceYaml, cancellationToken);
                var (runtimeYaml, result) = await _overrides.BuildAsync(tempPath, cancellationToken);
                await File.WriteAllTextAsync(tempPath, runtimeYaml, cancellationToken);
                await MihomoControllerEndpoint.PrepareConfigFileForCoreAsync(
                    tempPath,
                    cancellationToken);
                await _core.ValidateConfigAsync(tempPath, cancellationToken);
                File.Move(tempPath, AppPaths.RuntimeConfigPath, overwrite: true);
                return result;
            }
            finally
            {
                TryDeleteTemporaryFile(tempPath);
            }
        }
        finally
        {
            _composeLock.Release();
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException(
                LogSources.Core,
                ex,
                "清理运行时配置临时文件失败",
                "WARN");
        }
    }
}
