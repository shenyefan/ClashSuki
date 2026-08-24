$script:SupportedCulture = "zh-CN"

function Test-IsCultureDirectoryName
{
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    try
    {
        $culture = [System.Globalization.CultureInfo]::GetCultureInfo($Name)
        return -not $culture.IsNeutralCulture
    }
    catch [System.Globalization.CultureNotFoundException]
    {
        return $false
    }
}

function Remove-NonChineseLanguageResources
{
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($Root)
    $rootPrefix = $resolvedRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    foreach ($directory in Get-ChildItem -LiteralPath $resolvedRoot -Directory)
    {
        if (-not (Test-IsCultureDirectoryName -Name $directory.Name) -or
            $directory.Name.Equals($script:SupportedCulture, [System.StringComparison]::OrdinalIgnoreCase))
        {
            continue
        }

        $resolvedDirectory = [System.IO.Path]::GetFullPath($directory.FullName)
        if (-not $resolvedDirectory.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase))
        {
            throw "拒绝清理发布目录之外的语言资源：$resolvedDirectory"
        }

        $unexpectedFiles = @(Get-ChildItem -LiteralPath $resolvedDirectory -File -Recurse | Where-Object {
            -not $_.Name.EndsWith(".mui", [System.StringComparison]::OrdinalIgnoreCase) -and
            -not $_.Name.EndsWith(".resources.dll", [System.StringComparison]::OrdinalIgnoreCase)
        })
        if ($unexpectedFiles.Count -gt 0)
        {
            throw "语言目录包含非资源文件，拒绝删除：$($unexpectedFiles.FullName -join '、')"
        }

        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
}

function Assert-ChineseOnlyArchiveEntries
{
    param(
        [Parameter(Mandatory)]
        [System.Collections.IEnumerable]$Entries,

        [Parameter(Mandatory)]
        [string]$ArtifactName
    )

    $unexpectedCultures = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Entries)
    {
        foreach ($segment in $entry.FullName.Replace("\", "/").Split(
                     '/',
                     [System.StringSplitOptions]::RemoveEmptyEntries))
        {
            if ((Test-IsCultureDirectoryName -Name $segment) -and
                -not $segment.Equals($script:SupportedCulture, [System.StringComparison]::OrdinalIgnoreCase))
            {
                [void]$unexpectedCultures.Add($segment)
            }
        }
    }

    if ($unexpectedCultures.Count -gt 0)
    {
        throw "$ArtifactName 包含非中文语言资源：$(@($unexpectedCultures | Sort-Object) -join '、')"
    }
}
