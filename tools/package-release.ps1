param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$CertificateThumbprint,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src\SevenRecord.App\SevenRecord.App.csproj"
$artifactRoot = Join-Path $repositoryRoot "artifacts\release\$Version"
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

foreach ($architecture in @("x64", "arm64", "x86")) {
    dotnet build $project `
        --configuration $Configuration `
        -p:Platform=$architecture `
        -p:RuntimeIdentifier="win-$architecture" `
        -p:GenerateAppxPackageOnBuild=true `
        -p:AppxBundle=Never `
        -p:AppxPackageSigningEnabled=true `
        -p:PackageCertificateThumbprint=$CertificateThumbprint `
        -p:AppxPackageVersion=$Version
    if ($LASTEXITCODE -ne 0) {
        throw "7Record $architecture package build failed."
    }

    $package = Get-ChildItem `
        (Join-Path $repositoryRoot "src\SevenRecord.App\AppPackages") `
        -Filter "*.msix" `
        -File `
        -Recurse |
        Where-Object FullName -Match "_${architecture}_" |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if (-not $package) {
        throw "7Record $architecture MSIX was not generated."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $package.FullName
    if ($signature.Status -ne "Valid") {
        throw "7Record $architecture MSIX signature is $($signature.Status)."
    }

    $asset = Join-Path $artifactRoot "7Record-win-$architecture.msix"
    Copy-Item $package.FullName $asset -Force
    $hash = (Get-FileHash $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content `
        -Path "$asset.sha256" `
        -Value "$hash  $(Split-Path -Leaf $asset)" `
        -Encoding ascii
}

Get-ChildItem $artifactRoot -File | Select-Object Name, Length
