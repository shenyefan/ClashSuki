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

$buildDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = Split-Path -Parent $buildDirectory
. (Join-Path $buildDirectory "Build.Common.ps1")
$projectDirectory = Join-Path $repositoryRoot "ClashSuki.Package"
$projectPath = Join-Path $projectDirectory "ClashSuki.Package.wapproj"
$payloadManifest = Import-PowerShellDataFile -LiteralPath (Join-Path $buildDirectory "PayloadManifest.psd1")
$vswherePath = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"

[xml]$versionProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot "Directory.Build.props") -Raw
$assemblyVersionNode = $versionProperties.SelectSingleNode("/Project/PropertyGroup/AssemblyVersion")
if ($null -eq $assemblyVersionNode -or [string]::IsNullOrWhiteSpace($assemblyVersionNode.InnerText))
{
    throw "无法从 Directory.Build.props 读取 AssemblyVersion"
}

$assemblyVersion = $assemblyVersionNode.InnerText.Trim()
[xml]$packageManifest = Get-Content -LiteralPath (Join-Path $projectDirectory "Package.appxmanifest") -Raw
$packageIdentity = $packageManifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
[xml]$applicationManifest = Get-Content -LiteralPath (Join-Path $repositoryRoot "ClashSuki\app.manifest") -Raw
$applicationIdentity = $applicationManifest.SelectSingleNode("/*[local-name()='assembly']/*[local-name()='assemblyIdentity']")
if ($null -eq $packageIdentity -or
    $null -eq $applicationIdentity -or
    -not $assemblyVersion.Equals($packageIdentity.Version, [System.StringComparison]::Ordinal) -or
    -not $assemblyVersion.Equals($applicationIdentity.Version, [System.StringComparison]::Ordinal))
{
    throw "Directory.Build.props、Package.appxmanifest 与 app.manifest 的四段版本必须一致：$assemblyVersion"
}

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
    $arguments += "/p:PackageCertificateThumbprint="
}
else
{
    $resolvedCertificatePath = (Resolve-Path -LiteralPath $CertificatePath).Path
    $arguments += "/p:AppxPackageSigningEnabled=true"
    $arguments += "/p:PackageCertificateKeyFile=$resolvedCertificatePath"
    $arguments += "/p:PackageCertificateThumbprint="
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
    Assert-ChineseOnlyArchiveEntries -Entries $archive.Entries -ArtifactName "MSIX"
    $packageEntries = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries)
    {
        [void]$packageEntries.Add($entry.FullName.Replace("/", "\"))
    }

    $requiredVisualAssetEntries = @(Get-ChildItem `
        -LiteralPath (Join-Path $projectDirectory "..\ClashSuki\Assets\Visuals") `
        -Filter "*.png" `
        -File | ForEach-Object {
            "ClashSuki\Assets\Visuals\$($_.Name)"
        })

    $requiredEntries = @(
        "ClashSuki\ClashSuki.exe"
        "ClashSuki\ClashSuki.deps.json"
        "ClashSuki\ClashSuki.runtimeconfig.json"
        "ClashSuki\coreclr.dll"
        "ClashSuki\hostfxr.dll"
        "ClashSuki\hostpolicy.dll"
        "ClashSuki\System.Private.CoreLib.dll"
        "ClashSuki.Service\ClashSuki.Service.exe"
        "ClashSuki.Repair\ClashSuki.Repair.exe"
    ) + @($payloadManifest.RuntimeAssets | ForEach-Object { "ClashSuki\$_" }) + $requiredVisualAssetEntries
    $missingEntries = @($requiredEntries | Where-Object { -not $packageEntries.Contains($_) })
    if ($missingEntries.Count -gt 0)
    {
        throw "MSIX 缺少清单资源或运行时文件：$($missingEntries -join '、')"
    }

    $manifestEntry = $archive.GetEntry("AppxManifest.xml")
    if ($null -eq $manifestEntry)
    {
        throw "MSIX 缺少生成后的 AppxManifest.xml。"
    }

    $manifestStream = $manifestEntry.Open()
    $manifestReader = [System.IO.StreamReader]::new($manifestStream)
    try
    {
        [xml]$generatedManifest = $manifestReader.ReadToEnd()
    }
    finally
    {
        $manifestReader.Dispose()
        $manifestStream.Dispose()
    }

    $frameworkDependencies = @(
        $generatedManifest.SelectNodes(
            "/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']") |
        ForEach-Object { $_.Name }
    )
    $requiredFrameworkDependencies = @(
        "Microsoft.VCLibs.140.00"
        "Microsoft.VCLibs.140.00.UWPDesktop"
        "Microsoft.WindowsAppRuntime.2"
    )
    $missingFrameworkDependencies = @($requiredFrameworkDependencies | Where-Object {
        $_ -notin $frameworkDependencies
    })
    if ($missingFrameworkDependencies.Count -gt 0)
    {
        throw "MSIX 未声明官方框架依赖：$($missingFrameworkDependencies -join '、')"
    }

    $rootAssetEntries = @($packageEntries | Where-Object {
        $_.StartsWith("Assets\", [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($rootAssetEntries.Count -gt 0)
    {
        throw "MSIX 包根仍包含重复 Assets 目录：$($rootAssetEntries -join '、')"
    }

    $forbiddenEntries = @($payloadManifest.ForbiddenRuntimeAssets | ForEach-Object { "ClashSuki\$_" })
    $unexpectedEntries = @($forbiddenEntries | Where-Object { $packageEntries.Contains($_) })
    if ($unexpectedEntries.Count -gt 0)
    {
        throw "MSIX 包含已移除的运行时工具：$($unexpectedEntries -join '、')"
    }
}
finally
{
    $archive.Dispose()
}

Write-Host "MSIX 已生成并通过内容校验：$($package.FullName)" -ForegroundColor Green
