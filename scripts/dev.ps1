param(
    [ValidateSet('build','run','test','diagnose','battery','performance','publish')]
    [string]$Action = 'run'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdkPath = Join-Path $projectRoot '.tools\dotnet'
$dotnet = Join-Path $sdkPath 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Missing local SDK. Run scripts\setup.ps1 first.' }
$env:DOTNET_ROOT = $sdkPath
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
$app = Join-Path $projectRoot 'src\CherryDesk\CherryDesk.csproj'
Push-Location $projectRoot
try {
    switch ($Action) {
        'build' { & $dotnet build $app -c Release --nologo }
        'run' { & $dotnet run --project $app -c Release }
        'test' { & $dotnet run --project 'tests\CherryDesk.Tests\CherryDesk.Tests.csproj' -c Release }
        'diagnose' {
            New-Item -ItemType Directory -Force -Path 'work' | Out-Null
            & $dotnet run --project $app -c Release -- --diagnose (Join-Path $projectRoot 'work\audio-diagnostics.json')
        }
        'performance' {
            New-Item -ItemType Directory -Force -Path 'work' | Out-Null
            & $dotnet run --project $app -c Release -- --performance-diagnose (Join-Path $projectRoot 'work\performance-diagnostics.json')
        }
        'battery' {
            New-Item -ItemType Directory -Force -Path 'work' | Out-Null
            & $dotnet run --project $app -c Release -- --battery-diagnose (Join-Path $projectRoot 'work\battery-diagnostics.json')
        }
        'publish' {
            & $dotnet publish $app -c Release -r win-x64 --self-contained true `
                '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' `
                '-p:EnableCompressionInSingleFile=true' --source 'https://api.nuget.org/v3/index.json' -o 'artifacts\CherryDesk-win-x64'
            if ($LASTEXITCODE -eq 0) {
                Copy-Item 'README.md','THIRD-PARTY-NOTICES.md' 'artifacts\CherryDesk-win-x64'
                Copy-Item (Join-Path $sdkPath 'LICENSE.txt') 'artifacts\CherryDesk-win-x64\DOTNET-LICENSE.txt'
                Copy-Item (Join-Path $sdkPath 'ThirdPartyNotices.txt') 'artifacts\CherryDesk-win-x64\DOTNET-THIRD-PARTY-NOTICES.txt'
            }
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet $Action failed (exit $LASTEXITCODE)" }
} finally { Pop-Location }
