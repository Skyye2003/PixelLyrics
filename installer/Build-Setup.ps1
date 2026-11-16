param(
    [string]$Version = '0.2.0-rc.2',
    [Parameter(Mandatory=$true)][string]$ApplicationExe,
    [string]$Compiler = "$PSScriptRoot/../.tools/innosetup/tools/ISCC.exe"
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$inputDir = Join-Path $repo 'release/installer-input'
$sourceDir = Join-Path $repo 'release/component-source'
function Copy-Tree($Source, $Target) {
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Target -Recurse -Force
}
$bn = (Get-ChildItem (Join-Path $sourceDir 'BetterNCM') -Directory).FullName
$inf = (Get-ChildItem (Join-Path $sourceDir 'InfLink') -Directory).FullName
$cef = (Get-ChildItem (Join-Path $sourceDir 'libcef') -Directory).FullName
$js = (Get-ChildItem (Join-Path $sourceDir 'js-framework') -Directory).FullName
Copy-Tree $cef (Join-Path $bn 'src/3rd/libcef')
Copy-Tree $cef (Join-Path $inf 'libs/libcef')
Copy-Tree $js (Join-Path $bn 'src/js-framework')
$bundle = Join-Path $inputDir 'source-bundle'
Copy-Tree $bn (Join-Path $bundle 'BetterNCM-1.3.4')
Copy-Tree $inf (Join-Path $bundle 'InfLink-rs-3.4.0')
Copy-Tree ((Get-ChildItem (Join-Path $sourceDir 'PluginMarket') -Directory).FullName) (Join-Path $bundle 'PluginMarket-0.8.0')
Copy-Item (Join-Path $PSScriptRoot 'SOURCE.md') $bundle
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath (Join-Path $inputDir 'component-source.zip') -Force
New-Item -ItemType Directory -Path (Join-Path $inputDir 'licenses') -Force | Out-Null
Copy-Item (Join-Path $bn 'LICENSE') (Join-Path $inputDir 'licenses/BetterNCM-GPL-3.0.txt')
Copy-Item (Join-Path $inf 'LICENSE') (Join-Path $inputDir 'licenses/InfLink-GPL-3.0.txt')
Copy-Item (Join-Path $repo 'vendor-pixelbar/LICENSE') (Join-Path $inputDir 'licenses/PixelBar-MIT.txt')
Copy-Item (Join-Path $repo 'THIRD_PARTY.md') $inputDir
Copy-Item $ApplicationExe (Join-Path $inputDir 'PixelLyrics.exe')
Copy-Item (Join-Path $repo 'release/netease-research/BetterNCMII-1.3.4.dll') $inputDir
Copy-Item (Join-Path $repo 'release/netease-research/InfLink-rs-3.4.0.plugin') $inputDir
$expected = @{
    'BetterNCMII-1.3.4.dll'='a7c77af418d7940e63faa58ea036fba1f4baad497947109ea52ed78c8e86608f';
    'InfLink-rs-3.4.0.plugin'='7d0b7b863b072a99b88eb614c788099315e690920889aaca22685d3663661468';
    'vc_redist.x64.exe'='cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b'
}
foreach ($name in $expected.Keys) {
    if ((Get-FileHash (Join-Path $inputDir $name)).Hash.ToLowerInvariant() -ne $expected[$name]) { throw ('附件哈希不符：' + $name) }
}
& $Compiler ('/DPayload=' + $inputDir) ('/DAppVersion=' + $Version) (Join-Path $PSScriptRoot 'PixelLyrics.iss')
if ($LASTEXITCODE -ne 0) { throw '安装包编译失败。' }
Get-FileHash (Join-Path $repo ('release/setup/PixelLyrics-' + $Version + '-Setup-win-x64.exe'))
