# Builds release zips into .\dist
#   .\build.ps1            -> version from src\RecentFolders.csproj
#   .\build.ps1 -Version 1.2.0
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\RecentFolders.csproj'
$dist = Join-Path $root 'dist'

if (-not $Version) { $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Select-Object -First 1 }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null

function Publish([string]$rid, [bool]$selfContained, [string]$name) {
    $out = Join-Path $dist $name
    $sc = $selfContained.ToString().ToLower()
    dotnet publish $proj -c Release -r $rid --self-contained $sc `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=$sc -p:DebugType=none -p:Version=$Version -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $name" }
    Copy-Item (Join-Path $root 'src\default.conf') (Join-Path $out 'recent-folders.conf')
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath (Join-Path $dist "$name.zip")
}

# Small exe, needs the .NET 8 Desktop Runtime.
Publish 'win-x64' $false "recent-folders-$Version-win-x64"
# Bigger exe, no .NET install needed.
Publish 'win-x64' $true "recent-folders-$Version-win-x64-standalone"
Publish 'win-arm64' $true "recent-folders-$Version-win-arm64-standalone"

Get-ChildItem $dist -Filter *.zip | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } }
