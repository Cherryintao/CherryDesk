$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdkVersion = (Get-Content (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$sdkFolder = Join-Path $projectRoot '.tools\dotnet'
$sdkExecutable = Join-Path $sdkFolder 'dotnet.exe'
if (Test-Path -LiteralPath (Join-Path $sdkFolder "sdk\$sdkVersion")) {
    & $sdkExecutable --info
    exit $LASTEXITCODE
}
$scratch = Join-Path $projectRoot 'work'
New-Item -ItemType Directory -Force -Path $scratch,$sdkFolder | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$metadata = Invoke-RestMethod 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
$sdk = $metadata.releases | ForEach-Object { $_.sdks } | Where-Object { $_.version -eq $sdkVersion } | Select-Object -First 1
if (-not $sdk) { throw "SDK $sdkVersion not found in Microsoft's release metadata" }
$file = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
if (-not $file) { throw 'Windows x64 SDK archive not found' }
$archivePath = Join-Path $scratch "dotnet-sdk-$sdkVersion.zip"
Invoke-WebRequest $file.url -OutFile $archivePath -UseBasicParsing
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash -ne $file.hash) { throw 'SDK SHA512 verification failed' }
Expand-Archive -LiteralPath $archivePath -DestinationPath $sdkFolder -Force
& $sdkExecutable --info
if ($LASTEXITCODE -ne 0) { throw 'SDK installation verification failed' }
