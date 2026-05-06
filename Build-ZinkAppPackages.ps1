param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string[]]$Platforms = @("x64", "arm64"),

    [switch]$CreateBundle
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $projectDir "Zink.csproj"

$selectedPlatforms = $Platforms |
    ForEach-Object { $_ -split "," } |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ } |
    ForEach-Object {
        if ($_ -ieq "ARM64") { "arm64" }
        elseif ($_ -ieq "X64") { "x64" }
        elseif ($_ -ieq "X86") { "x86" }
        else { throw "Unsupported platform '$_'." }
    } |
    Select-Object -Unique

if ($selectedPlatforms.Count -eq 0) {
    throw "At least one platform must be selected."
}

$bundlePlatforms = $selectedPlatforms -join "|"
$packageRoot = Join-Path $env:LOCALAPPDATA "Zink\PackageOutput"
$packageDir = Join-Path $packageRoot ("PackageBuild-" + (Get-Date -Format "yyyyMMdd-HHmmss"))

Write-Host "Building Zink MSIX packages for: $bundlePlatforms"
Write-Host "Configuration: $Configuration"
Write-Host "Package directory: $packageDir"

foreach ($platform in $selectedPlatforms) {
    $runtimeIdentifier = if ($platform -eq "x86") { "win-x86" } elseif ($platform -eq "arm64") { "win-arm64" } else { "win-x64" }
    $platformPackageDir = ((Join-Path $packageDir $platform) + "/")

    Write-Host ""
    Write-Host "Restoring $platform ($runtimeIdentifier)..."
    dotnet restore $projectPath -p:Platform=$platform -p:RuntimeIdentifier=$runtimeIdentifier
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "Building package for $platform..."
    dotnet build $projectPath `
        -c $Configuration `
        -p:Platform=$platform `
        -p:RuntimeIdentifier=$runtimeIdentifier `
        -p:GenerateAppxPackageOnBuild=true `
        -p:AppxBundle=Never `
        "-p:AppxPackageDir=$platformPackageDir"

    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($CreateBundle -and $selectedPlatforms.Count -gt 1) {
    $primaryPlatform = if ($selectedPlatforms -contains "x64") { "x64" } else { $selectedPlatforms[0] }
    $bundlePackageDir = ((Join-Path $packageDir "bundle") + "/")

    Write-Host ""
    Write-Host "Building combined bundle for $bundlePlatforms..."
    dotnet build $projectPath `
        -c $Configuration `
        -p:Platform=$primaryPlatform `
        -p:GenerateAppxPackageOnBuild=true `
        -p:AppxBundle=Always `
        "-p:AppxPackageDir=$bundlePackageDir" `
        "-p:AppxBuildConfigurationSelection=$bundlePlatforms" `
        "-p:AppxBundlePlatforms=$bundlePlatforms"

    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Package output:"
Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
    Where-Object { $_.Extension -in ".msixbundle", ".msix", ".cer" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 10 FullName, Length, LastWriteTime |
    Format-Table -AutoSize
