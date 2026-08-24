[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64",

    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
# Portable 构建独立于 WAP 项目，脚本从仓库 build 目录运行。

function Assert-RequiredFiles
{
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string[]]$RelativePaths
    )

    $missingFiles = @($RelativePaths | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $Root $_) -PathType Leaf)
    })
    if ($missingFiles.Count -gt 0)
    {
        throw "Portable 发布目录缺少运行时文件：$($missingFiles -join '、')"
    }

    $emptyFiles = @($RelativePaths | Where-Object {
        (Get-Item -LiteralPath (Join-Path $Root $_)).Length -eq 0
    })
    if ($emptyFiles.Count -gt 0)
    {
        throw "Portable 发布目录包含空文件：$($emptyFiles -join '、')"
    }
}

function Test-IsForbiddenPortableFileName
{
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$FileName
    )

    return (
        $FileName.EndsWith(".pdb", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.EndsWith(".appx", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.EndsWith(".appxbundle", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.EndsWith(".appinstaller", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.EndsWith(".appxmanifest", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.EndsWith(".msix", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.EndsWith(".msixbundle", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.Equals("enableLoopback.exe", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.Equals("age-inspect.exe", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.Equals("age-plugin-batchpass.exe", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.Equals("AppxManifest.xml", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.Equals("AppxBlockMap.xml", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.Equals("AppxSignature.p7x", [System.StringComparison]::OrdinalIgnoreCase) -or
        $FileName.Equals("[Content_Types].xml", [System.StringComparison]::OrdinalIgnoreCase))
}

function Assert-NoForbiddenPortableFiles
{
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo[]]$Files
    )

    $unexpectedFiles = @($Files | Where-Object {
        Test-IsForbiddenPortableFileName -FileName $_.Name
    } | Select-Object -ExpandProperty FullName)

    if ($unexpectedFiles.Count -gt 0)
    {
        throw "Portable 不得包含 MSIX 修复程序、调试符号、包元数据或已移除工具：$($unexpectedFiles -join '、')"
    }
}

function Assert-ServiceInstallerLayout
{
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [System.IO.FileInfo[]]$Files
    )

    $expectedServicePath = [System.IO.Path]::GetFullPath(
        (Join-Path $Root "ServiceInstaller\ClashSuki.Service.exe"))
    $expectedRepairPath = [System.IO.Path]::GetFullPath(
        (Join-Path $Root "ServiceInstaller\ClashSuki.Repair.exe"))
    $serviceExecutables = @($Files | Where-Object {
        $_.Name.Equals("ClashSuki.Service.exe", [System.StringComparison]::OrdinalIgnoreCase)
    })

    if ($serviceExecutables.Count -ne 1 -or
        -not $serviceExecutables[0].FullName.Equals(
            $expectedServicePath,
            [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Portable 必须且只能在 ServiceInstaller 目录包含一份 ClashSuki.Service.exe。"
    }

    $repairExecutables = @($Files | Where-Object {
        $_.Name.Equals("ClashSuki.Repair.exe", [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($repairExecutables.Count -ne 1 -or
        -not $repairExecutables[0].FullName.Equals(
            $expectedRepairPath,
            [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Portable 必须且只能在 ServiceInstaller 目录包含一份 ClashSuki.Repair.exe。"
    }

    $unexpectedInstallerFiles = @($Files | Where-Object {
        ($_.Name.StartsWith("ClashSuki.Service.", [System.StringComparison]::OrdinalIgnoreCase) -and
         -not $_.Name.Equals("ClashSuki.Service.exe", [System.StringComparison]::OrdinalIgnoreCase)) -or
        ($_.Name.StartsWith("ClashSuki.Repair.", [System.StringComparison]::OrdinalIgnoreCase) -and
         -not $_.Name.Equals("ClashSuki.Repair.exe", [System.StringComparison]::OrdinalIgnoreCase))
    } | Select-Object -ExpandProperty FullName)
    if ($unexpectedInstallerFiles.Count -gt 0)
    {
        throw "Portable 服务与安装器必须为单文件发布，不得包含旁加载文件：$($unexpectedInstallerFiles -join '、')"
    }
}

$projectDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = Split-Path -Parent $projectDirectory
. (Join-Path $projectDirectory "Build.Common.ps1")
$payloadManifest = Import-PowerShellDataFile -LiteralPath (Join-Path $projectDirectory "PayloadManifest.psd1")
$versionPropertiesPath = Join-Path $repositoryRoot "Directory.Build.props"
$appProjectPath = Join-Path $repositoryRoot "ClashSuki\ClashSuki.csproj"
$serviceProjectPath = Join-Path $repositoryRoot "ClashSuki.Service\ClashSuki.Service.csproj"
$repairProjectPath = Join-Path $repositoryRoot "ClashSuki.Repair\ClashSuki.Repair.csproj"
$runtimeIdentifier = "win-$Platform"

[xml]$versionProperties = Get-Content -LiteralPath $versionPropertiesPath -Raw
$versionNode = $versionProperties.SelectSingleNode("/Project/PropertyGroup/Version")
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText))
{
    throw "无法从 Directory.Build.props 读取 Version"
}

function Add-AppLocalVcRuntime
{
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Architecture,

        [Parameter(Mandatory)]
        [string[]]$FileNames
    )

    $extensionSdkRoot = Join-Path `
        ${env:ProgramFiles(x86)} `
        "Microsoft SDKs\Windows Kits\10\ExtensionSDKs\Microsoft.VCLibs.Desktop"
    $packageName = "Microsoft.VCLibs.$Architecture.14.00.Desktop.appx"
    $runtimePackage = Get-ChildItem `
        -LiteralPath $extensionSdkRoot `
        -Filter $packageName `
        -File `
        -Recurse `
        -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName.Contains(
                "\Appx\Retail\",
                [System.StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $runtimePackage)
    {
        throw "未找到官方 Microsoft.VCLibs.Desktop x64 运行库，请安装 Windows SDK 的 UWP C++ 运行时组件。"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($runtimePackage.FullName)
    try
    {
        foreach ($fileName in $FileNames)
        {
            $entry = $archive.Entries | Where-Object {
                $_.FullName.Equals($fileName, [System.StringComparison]::OrdinalIgnoreCase)
            } | Select-Object -First 1
            if ($null -eq $entry -or $entry.Length -eq 0)
            {
                throw "$($runtimePackage.FullName) 缺少运行库文件：$fileName"
            }

            $source = $entry.Open()
            $destination = [System.IO.File]::Create((Join-Path $Root $fileName))
            try
            {
                $source.CopyTo($destination)
            }
            finally
            {
                $destination.Dispose()
                $source.Dispose()
            }
        }
    }
    finally
    {
        $archive.Dispose()
    }
}

$version = $versionNode.InnerText.Trim()
if ($version.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0)
{
    throw "Directory.Build.props 中的 Version 不能用于产物文件名：$version"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    $OutputDirectory = Join-Path $projectDirectory "Portable"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory))
{
    $OutputDirectory = Join-Path $repositoryRoot $OutputDirectory
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$archiveName = "ClashSuki-$version-$runtimeIdentifier-portable.zip"
$archivePath = Join-Path $OutputDirectory $archiveName
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "ClashSuki-Portable-$([Guid]::NewGuid().ToString('N'))"
$publishDirectory = Join-Path $temporaryRoot "publish"
$servicePublishDirectory = Join-Path $temporaryRoot "service-publish"
$repairPublishDirectory = Join-Path $temporaryRoot "repair-publish"
$serviceInstallerDirectory = Join-Path $publishDirectory "ServiceInstaller"
$vcRuntimeFiles = @(
    "msvcp140.dll"
    "vcruntime140.dll"
    "vcruntime140_1.dll"
)

$requiredFiles = @(
    "ClashSuki.exe"
    "ClashSuki.dll"
    "ClashSuki.deps.json"
    "ClashSuki.runtimeconfig.json"
    "coreclr.dll"
    "hostfxr.dll"
    "hostpolicy.dll"
    "System.Private.CoreLib.dll"
    "Microsoft.WindowsAppRuntime.dll"
    "Microsoft.ui.xaml.dll"
    "Microsoft.UI.Xaml.Controls.pri"
    "ServiceInstaller\ClashSuki.Service.exe"
    "ServiceInstaller\ClashSuki.Repair.exe"
    "PORTABLE.txt"
) + $vcRuntimeFiles + @($payloadManifest.RuntimeAssets)

$portableNotice = @"
ClashSuki Portable $version ($runtimeIdentifier)

将 ZIP 完整解压后运行 ClashSuki.exe。本版本同时携带 .NET、Windows App SDK 与
应用本地 Visual C++ 运行库，不需要在目标系统另行安装这些运行环境。

普通代理和系统代理无需安装服务。首次使用 TUN 虚拟网卡时，请在“虚拟网卡”页面
点击“安装服务”并确认 Windows 用户账户控制提示。商店应用代理只在保存回环权限时
请求一次管理员权限，不会安装服务。

请保持 ServiceInstaller 与 Assets 目录结构完整；修复程序会使用
ServiceInstaller\ClashSuki.Service.exe 和 Assets\Core\mihomo.exe 安装便携服务。
"@

try
{
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $servicePublishDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $repairPublishDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $serviceInstallerDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $archivePath)
    {
        Remove-Item -LiteralPath $archivePath -Force
    }

    $appPublishArguments = @(
        "publish"
        $appProjectPath
        "--configuration"
        $Configuration
        "--runtime"
        $runtimeIdentifier
        "--self-contained"
        "true"
        "--output"
        $publishDirectory
        "/p:Platform=$Platform"
        "/p:PublishProfile="
        "/p:WindowsPackageType=None"
        "/p:WindowsAppSDKSelfContained=true"
        "/p:SelfContained=true"
        "/p:UseAppHost=true"
        "/p:PublishSingleFile=false"
        "/p:PublishTrimmed=false"
        "/p:PublishReadyToRun=false"
        "/p:DebugSymbols=false"
        "/p:DebugType=None"
    )

    & dotnet @appPublishArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Portable 主程序发布失败，dotnet publish 退出码：$LASTEXITCODE"
    }

    Get-ChildItem -LiteralPath $publishDirectory -Filter "*.pdb" -File -Recurse |
        Remove-Item -Force
    Remove-NonChineseLanguageResources -Root $publishDirectory
    Add-AppLocalVcRuntime `
        -Root $publishDirectory `
        -Architecture $Platform `
        -FileNames $vcRuntimeFiles

    $servicePublishArguments = @(
        "publish"
        $serviceProjectPath
        "--configuration"
        $Configuration
        "--runtime"
        $runtimeIdentifier
        "--self-contained"
        "true"
        "--output"
        $servicePublishDirectory
        "/p:Platform=$Platform"
        "/p:PublishProfile="
        "/p:SelfContained=true"
        "/p:UseAppHost=true"
        "/p:PublishSingleFile=true"
        "/p:IncludeNativeLibrariesForSelfExtract=true"
        "/p:EnableCompressionInSingleFile=true"
        "/p:PublishTrimmed=false"
        "/p:PublishReadyToRun=false"
        "/p:DebugSymbols=false"
        "/p:DebugType=None"
    )

    & dotnet @servicePublishArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Portable 服务发布失败，dotnet publish 退出码：$LASTEXITCODE"
    }

    Get-ChildItem -LiteralPath $servicePublishDirectory -Filter "*.pdb" -File -Recurse |
        Remove-Item -Force

    $servicePublishFiles = @(Get-ChildItem -LiteralPath $servicePublishDirectory -File -Recurse)
    if ($servicePublishFiles.Count -ne 1 -or
        -not $servicePublishFiles[0].Name.Equals(
            "ClashSuki.Service.exe",
            [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Portable 服务没有生成预期的自包含单文件：$($servicePublishFiles.FullName -join '、')"
    }

    Copy-Item -LiteralPath $servicePublishFiles[0].FullName `
        -Destination (Join-Path $serviceInstallerDirectory "ClashSuki.Service.exe")

    $repairPublishArguments = @(
        "publish"
        $repairProjectPath
        "--configuration"
        $Configuration
        "--runtime"
        $runtimeIdentifier
        "--self-contained"
        "true"
        "--output"
        $repairPublishDirectory
        "/p:Platform=$Platform"
        "/p:PublishProfile="
        "/p:SelfContained=true"
        "/p:UseAppHost=true"
        "/p:PublishSingleFile=true"
        "/p:IncludeNativeLibrariesForSelfExtract=true"
        "/p:EnableCompressionInSingleFile=true"
        "/p:PublishTrimmed=false"
        "/p:PublishReadyToRun=false"
        "/p:DebugSymbols=false"
        "/p:DebugType=None"
    )

    & dotnet @repairPublishArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Portable 服务安装器发布失败，dotnet publish 退出码：$LASTEXITCODE"
    }

    Get-ChildItem -LiteralPath $repairPublishDirectory -Filter "*.pdb" -File -Recurse |
        Remove-Item -Force

    $repairPublishFiles = @(Get-ChildItem -LiteralPath $repairPublishDirectory -File -Recurse)
    if ($repairPublishFiles.Count -ne 1 -or
        -not $repairPublishFiles[0].Name.Equals(
            "ClashSuki.Repair.exe",
            [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Portable 服务安装器没有生成预期的自包含单文件：$($repairPublishFiles.FullName -join '、')"
    }

    Copy-Item -LiteralPath $repairPublishFiles[0].FullName `
        -Destination (Join-Path $serviceInstallerDirectory "ClashSuki.Repair.exe")

    [System.IO.File]::WriteAllText(
        (Join-Path $publishDirectory "PORTABLE.txt"),
        $portableNotice,
        [System.Text.UTF8Encoding]::new($false))

    Assert-RequiredFiles -Root $publishDirectory -RelativePaths $requiredFiles
    $portableFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
    Assert-NoForbiddenPortableFiles -Files $portableFiles
    Assert-ServiceInstallerLayout -Root $publishDirectory -Files $portableFiles

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDirectory,
        $archivePath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try
    {
        Assert-ChineseOnlyArchiveEntries -Entries $archive.Entries -ArtifactName "Portable ZIP"
        $archiveEntries = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries)
        {
            $normalizedEntryName = $entry.FullName.Replace("\", "/")
            if (-not $archiveEntries.Add($normalizedEntryName))
            {
                throw "Portable ZIP 包含重复条目：$normalizedEntryName"
            }
        }

        $missingArchiveEntries = @($requiredFiles | ForEach-Object { $_.Replace("\", "/") } |
            Where-Object { -not $archiveEntries.Contains($_) })
        if ($missingArchiveEntries.Count -gt 0)
        {
            throw "Portable ZIP 缺少运行时文件：$($missingArchiveEntries -join '、')"
        }

        $unexpectedArchiveEntries = @($archiveEntries | Where-Object {
            $fileName = ($_ -split "/")[-1]
            (Test-IsForbiddenPortableFileName -FileName $fileName) -or
            $_.StartsWith("AppxMetadata/", [System.StringComparison]::OrdinalIgnoreCase) -or
            $_.StartsWith("ClashSuki.Repair/", [System.StringComparison]::OrdinalIgnoreCase)
        })
        if ($unexpectedArchiveEntries.Count -gt 0)
        {
            throw "Portable ZIP 混入 MSIX 修复程序、调试符号、包元数据或已移除工具：$($unexpectedArchiveEntries -join '、')"
        }

        $mainExecutableEntries = @($archiveEntries | Where-Object {
            (($_ -split "/")[-1]).Equals(
                "ClashSuki.exe",
                [System.StringComparison]::OrdinalIgnoreCase)
        })
        if ($mainExecutableEntries.Count -ne 1 -or
            -not $mainExecutableEntries[0].Equals(
                "ClashSuki.exe",
                [System.StringComparison]::OrdinalIgnoreCase))
        {
            throw "Portable ZIP 必须在根目录且只能包含一份 ClashSuki.exe。"
        }

        $serviceArchiveEntries = @($archiveEntries | Where-Object {
            (($_ -split "/")[-1]).Equals(
                "ClashSuki.Service.exe",
                [System.StringComparison]::OrdinalIgnoreCase)
        })
        if ($serviceArchiveEntries.Count -ne 1 -or
            -not $serviceArchiveEntries[0].Equals(
                "ServiceInstaller/ClashSuki.Service.exe",
                [System.StringComparison]::OrdinalIgnoreCase))
        {
            throw "Portable ZIP 的服务安装程序目录结构无效。"
        }

        $repairArchiveEntries = @($archiveEntries | Where-Object {
            (($_ -split "/")[-1]).Equals(
                "ClashSuki.Repair.exe",
                [System.StringComparison]::OrdinalIgnoreCase)
        })
        if ($repairArchiveEntries.Count -ne 1 -or
            -not $repairArchiveEntries[0].Equals(
                "ServiceInstaller/ClashSuki.Repair.exe",
                [System.StringComparison]::OrdinalIgnoreCase))
        {
            throw "Portable ZIP 的服务安装器目录结构无效。"
        }

        $nestedPayloadEntries = @($archiveEntries | Where-Object {
            $_.StartsWith("publish/", [System.StringComparison]::OrdinalIgnoreCase) -or
            $_.StartsWith("ClashSuki/", [System.StringComparison]::OrdinalIgnoreCase) -or
            (($_ -match "(^|/)Assets/(Branding|Tray|Age|Core|Fonts|GeoData)/") -and
                -not $_.StartsWith("Assets/", [System.StringComparison]::OrdinalIgnoreCase))
        })
        if ($nestedPayloadEntries.Count -gt 0)
        {
            throw "Portable ZIP 根目录发生二次封装或包含重复 Assets：$($nestedPayloadEntries -join '、')"
        }
    }
    finally
    {
        $archive.Dispose()
    }

    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    Write-Host "Portable 已生成并通过内容校验：$archivePath" -ForegroundColor Green
    Write-Host "SHA256：$archiveHash" -ForegroundColor Green

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT))
    {
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "version=$version"
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "zip_path=$archivePath"
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "sha256=$archiveHash"
    }
}
catch
{
    Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
    throw
}
finally
{
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
