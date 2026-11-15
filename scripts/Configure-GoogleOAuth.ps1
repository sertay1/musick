param([Parameter(Mandatory=$true)][string]$CredentialsPath)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$document = Get-Content -LiteralPath $CredentialsPath -Raw | ConvertFrom-Json
if (-not $document.installed -or $document.web) { throw 'Google Desktop app türünde istemci JSON dosyası gerekli.' }
$native = $document.installed
if ($native.client_id -notmatch '^[0-9]+-[a-zA-Z0-9_-]+\.apps\.googleusercontent\.com$' -or -not $native.client_secret) { throw 'Google masaüstü istemci bilgileri geçersiz.' }
$config = @{ GoogleOAuth = @{ ClientId = $native.client_id; ClientSecret = $native.client_secret } } | ConvertTo-Json -Depth 3
$target = Join-Path $repo 'src/MusicBot.Web/google-oauth.desktop.json'
[IO.File]::WriteAllText($target, $config, [Text.UTF8Encoding]::new($false))
Write-Host 'Musick ortak Google masaüstü istemcisi yapılandırıldı. Kimlik değerleri çıktıya yazılmadı.'
