param(
    [switch]$Test,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$OutputDirectory,
    [switch]$NoArchive
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) { throw 'Install .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0' }
    $sdk = $command.Source
}
$version = & $sdk --version
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^10\.') { throw '.NET 10 SDK is required.' }
if ($Test) {
    & $sdk run --project (Join-Path $PSScriptRoot 'tests\NowPlaying.Tests') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & $sdk run --project (Join-Path $PSScriptRoot 'tests\NowPlaying.Windows.Tests') -c Release -- (Join-Path $PSScriptRoot 'artifacts\ui-preview.png')
    if ($LASTEXITCODE -ne 0) { throw 'Windows smoke tests failed.' }
}
$destination = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot "artifacts\Synko-$Runtime" }
& $sdk publish (Join-Path $PSScriptRoot 'src\NowPlaying.Windows\NowPlaying.Windows.csproj') -c Release -r $Runtime --self-contained true -o $destination --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $destination
$licenses = Join-Path $destination 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\NowPlaying.Windows\Ui\fonts\RobotoFlex-OFL.txt'), (Join-Path $PSScriptRoot 'src\NowPlaying.Windows\Ui\fonts\MaterialSymbols-LICENSE.txt'), (Join-Path $PSScriptRoot 'src\NowPlaying.Windows\Ui\assets\simple-icons-LICENSE.md') -Destination $licenses
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\NowPlaying.Windows\Ui\vendor\material-color-utilities\LICENSE') -Destination (Join-Path $licenses 'MaterialColorUtilities-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\NowPlaying.Windows\Ui\assets\browser-logos-LICENSE.txt'), (Join-Path $PSScriptRoot 'src\NowPlaying.Windows\Ui\assets\waterfox-LICENSE.txt') -Destination $licenses
$documentation = Join-Path $destination 'docs'
New-Item -ItemType Directory -Force -Path $documentation | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\images') -Destination $documentation -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\third-party') -Destination $documentation -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\TESTING.md'), (Join-Path $PSScriptRoot 'docs\PUBLISHING.md') -Destination $documentation -Force
$version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\NowPlaying.Windows\NowPlaying.Windows.csproj') -Raw)).Project.PropertyGroup.Version
$releaseExe = Join-Path $PSScriptRoot "artifacts\Synko-$version-$Runtime.exe"
Copy-Item -LiteralPath (Join-Path $destination 'Synko.exe') -Destination $releaseExe -Force
$hash = (Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256).Hash.ToLowerInvariant()
@{ version = [string]$version; runtime = $Runtime; executableBytes = (Get-Item -LiteralPath $releaseExe).Length; sha256 = $hash } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'build-info.json') -Encoding UTF8
"$hash  Synko-$version-$Runtime.exe" | Set-Content -LiteralPath (Join-Path $PSScriptRoot "artifacts\Synko-$version-$Runtime.sha256") -Encoding ascii
$archive = Join-Path $PSScriptRoot "artifacts\Synko-$Runtime.zip"
if (-not $NoArchive) { Compress-Archive -Path (Join-Path $destination 'Synko.exe'), (Join-Path $destination 'README.md'), $licenses, $documentation -DestinationPath $archive -Force }
Write-Output "Executable: $destination\Synko.exe"
Write-Output "Release asset: $releaseExe"
if (-not $NoArchive) { Write-Output "Archive: $archive" }
