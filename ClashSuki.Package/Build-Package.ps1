[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64",

    [string]$CertificatePath,

    [string]$CertificatePassword,

    [string]$AppInstallerUri
)

$ErrorActionPreference = "Stop"

$projectDirectory = Split-Path -Parent $PSCommandPath
$projectPath = Join-Path $projectDirectory "ClashSuki.Package.wapproj"
$vswherePath = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path -LiteralPath $vswherePath))
{
    throw "未找到 vswhere.exe，请安装包含 Windows Application Packaging Project 的 Visual Studio。"
}

$registeredInstallationPaths = @(& $vswherePath `
    -all `
    -products * `
    -requires Microsoft.Component.MSBuild `
    -property installationPath)

$visualStudioRoots = @(
    (Join-Path $env:ProgramFiles "Microsoft Visual Studio")
    (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio")
)
$discoveredInstallationPaths = foreach ($root in $visualStudioRoots)
{
    if (-not (Test-Path -LiteralPath $root))
    {
        continue
    }

    Get-ChildItem -LiteralPath $root -Directory |
        Get-ChildItem -Directory |
        Select-Object -ExpandProperty FullName
}

$installationPath = @($registeredInstallationPaths; $discoveredInstallationPaths) |
    Select-Object -Unique |
    Where-Object {
        (Test-Path -LiteralPath (Join-Path $_ "MSBuild\Current\Bin\MSBuild.exe")) -and
        (Test-Path -LiteralPath (Join-Path $_ "MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.props"))
    } |
    Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($installationPath))
{
    throw "未找到包含 Windows Application Packaging Project 工具的 Visual Studio。"
}

$msbuildPath = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path -LiteralPath $msbuildPath))
{
    throw "未找到 MSBuild.exe：$msbuildPath"
}

$arguments = @(
    $projectPath
    "/restore"
    "/t:Rebuild"
    "/m:1"
    "/nr:false"
    "/p:Configuration=$Configuration"
    "/p:Platform=$Platform"
    "/p:GenerateAppxPackageOnBuild=true"
)

if ([string]::IsNullOrWhiteSpace($CertificatePath))
{
    $arguments += "/p:AppxPackageSigningEnabled=false"
}
else
{
    $resolvedCertificatePath = (Resolve-Path -LiteralPath $CertificatePath).Path
    $arguments += "/p:AppxPackageSigningEnabled=true"
    $arguments += "/p:PackageCertificateKeyFile=$resolvedCertificatePath"
    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword))
    {
        $arguments += "/p:PackageCertificatePassword=$CertificatePassword"
    }
}

if (-not [string]::IsNullOrWhiteSpace($AppInstallerUri))
{
    $arguments += "/p:AppInstallerUri=$AppInstallerUri"
}

& $msbuildPath @arguments
if ($LASTEXITCODE -ne 0)
{
    throw "MSIX 生成失败，MSBuild 退出码：$LASTEXITCODE"
}

$packageDirectory = Join-Path $projectDirectory "AppPackages"
$package = Get-ChildItem -LiteralPath $packageDirectory -Filter "*.msix" -File -Recurse |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if ($null -eq $package)
{
    throw "MSIX 生成完成，但未在 ClashSuki.Package\\AppPackages 中找到安装包"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try
{
    $packageEntries = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries)
    {
        [void]$packageEntries.Add($entry.FullName.Replace("/", "\"))
    }

    $requiredEntries = @(
        "ClashSuki\ClashSuki.exe"
        "ClashSuki\ClashSuki.runtimeconfig.json"
        "ClashSuki.Service\ClashSuki.Service.exe"
        "ClashSuki.Service\ClashSuki.Service.runtimeconfig.json"
    )
    $missingEntries = @($requiredEntries | Where-Object { -not $packageEntries.Contains($_) })
    if ($missingEntries.Count -gt 0)
    {
        throw "MSIX 缺少清单入口或运行时文件：$($missingEntries -join '、')"
    }
}
finally
{
    $archive.Dispose()
}

Write-Host "MSIX 已生成并通过入口校验：$($package.FullName)" -ForegroundColor Green
