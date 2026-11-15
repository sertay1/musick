param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0',
    [ValidateSet('win-x64', 'win-x86')][string]$Runtime = 'win-x64',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
$output = Join-Path $artifactRoot "$Version/$Runtime"
$publish = [IO.Path]::GetFullPath((Join-Path $output 'app'))
$releases = Join-Path $output 'releases'
if (-not $publish.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Geçersiz çıktı yolu.' }
function Run([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable başarısız: $LASTEXITCODE" }
}
$previousRollForward = $env:DOTNET_ROLL_FORWARD
Push-Location $repo
try {
    Push-Location (Join-Path $repo 'src/MusicBot.Web/clientapp')
    try { Run 'npm.cmd' @('ci'); Run 'npm.cmd' @('run', 'build') } finally { Pop-Location }
    if (-not $SkipTests) { Run 'dotnet' @('test', 'src/MusicBot.Tests/MusicBot.Tests.csproj', '--configuration', 'Release', '--filter', 'FullyQualifiedName!~LiveTests') }
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
    Run 'dotnet' @('publish', 'src/MusicBot.Desktop/MusicBot.Desktop.csproj', '--configuration', 'Release', '--runtime', $Runtime, '--self-contained', 'true', '--output', $publish, "-p:Version=$Version")
    if (-not (Test-Path (Join-Path $publish 'wwwroot/index.html'))) { throw 'Web arayüzü yayımlama çıktısında bulunamadı.' }
    $env:DOTNET_ROLL_FORWARD = 'Major'
    Run 'dotnet' @('tool', 'restore')
    Run 'dotnet' @('tool', 'run', 'vpk', 'pack', '--packId', 'MusicBot', '--packVersion', $Version, '--packTitle', 'Musick', '--packAuthors', 'SertAy', '--runtime', $Runtime, '--framework', 'webview2', '--packDir', $publish, '--outputDir', $releases, '--mainExe', 'MusicBot.exe', '--icon', (Join-Path $repo 'src/MusicBot.Desktop/Resources/icon.ico'), '--noPortable', '--skip-updates', '--yes')
    Copy-Item -LiteralPath (Join-Path $releases 'MusicBot-win-Setup.exe') -Destination (Join-Path $output 'Musick-Setup.exe') -Force
    Write-Host "Hazır: $(Join-Path $output 'Musick-Setup.exe')"
} finally { $env:DOTNET_ROLL_FORWARD = $previousRollForward; Pop-Location }
