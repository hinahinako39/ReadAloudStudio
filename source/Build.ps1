param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Desktop/ReadAloudStudio.csproj'
$staging = Join-Path $PSScriptRoot 'Desktop/bin/publish-win-x64'
& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $staging
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$executable = Join-Path (Split-Path $PSScriptRoot) 'ReadAloudStudio.exe'
Copy-Item -LiteralPath (Join-Path $staging 'ReadAloudStudio.exe') -Destination $executable -Force
Write-Host $executable
