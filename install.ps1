param([string]$GameRoot = (Join-Path $PSScriptRoot '..\..'))
$ErrorActionPreference = 'Stop'
$target = (Resolve-Path -LiteralPath $GameRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $target 'KingdomInc.exe'))) { throw 'Expected Kingdom Inc game root.' }
if (Get-Process -Name KingdomInc -ErrorAction SilentlyContinue) { throw 'Close Kingdom Inc before installing.' }
$loader = Join-Path $target 'BepInEx\core\BepInEx.dll'
if (-not (Test-Path -LiteralPath $loader)) { throw 'A compatible BepInEx 5 loader is required. The archived loader can be restored separately.' }
$version = [Reflection.AssemblyName]::GetAssemblyName($loader).Version
if ($version.Major -ne 5 -or $version -lt [version]'5.4.23.5') { throw 'Requires BepInEx 5.4.23.5 or newer in the 5.x series.' }
$plugin = Join-Path $PSScriptRoot 'bin\Release\netstandard2.1\KingdomFactoryApi.dll'
if (-not (Test-Path -LiteralPath $plugin)) { throw 'Build the API in Release mode first.' }
$destinationDirectory = Join-Path $target 'BepInEx\plugins\KingdomFactoryApi'
New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
$destination = Join-Path $destinationDirectory 'KingdomFactoryApi.dll'
if (Test-Path -LiteralPath $destination) { Copy-Item -LiteralPath $destination -Destination ($destination + '.bak') -Force }
Copy-Item -LiteralPath $plugin -Destination $destination -Force
$pluginVersion = [Reflection.AssemblyName]::GetAssemblyName($destination).Version.ToString(3)
@{version=$pluginVersion; installedAt=(Get-Date).ToString('o');sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destinationDirectory 'installation.json')
Write-Output 'Factory API installed. Launch Kingdom Inc: http://127.0.0.1:17840/api/v1'
