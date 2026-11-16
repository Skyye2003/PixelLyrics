param(
    [ValidateSet('Install','Uninstall')][string]$Action = 'Install',
    [string]$CloudMusicDir,
    [string]$ProfileDir,
    [Parameter(Mandatory=$true)][string]$StateDir,
    [string]$PayloadDir = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'

function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-PlainPath([string]$Path) {
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path.StartsWith('\\')) { throw '请选择本机绝对路径。' }
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '安装路径不能包含符号链接或目录联接。' }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
function Assert-PlayerClosed {
    if (Get-Process cloudmusic,cloudmusic_reporter -ErrorAction SilentlyContinue) { throw '请先从托盘完全退出网易云音乐，再重试。' }
}
function Read-State {
    if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json }
}
function Write-State($State) {
    New-Item -ItemType Directory -Path $StateDir -Force | Out-Null
    $State | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath ($statePath + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($statePath + '.tmp') -Destination $statePath -Force
}

try {
    Assert-PlainPath $StateDir
    $statePath = Join-Path $StateDir 'netease-components.json'
    $state = Read-State
    Assert-PlayerClosed
    if ($Action -eq 'Uninstall') {
        if ($state) {
            $sharedPlugins = @()
            $pluginsPath = Join-Path $state.ProfileDir 'plugins'
            if (Test-Path -LiteralPath $pluginsPath) {
                $sharedPlugins = @(Get-ChildItem -LiteralPath $pluginsPath -File -Filter '*.plugin' | Where-Object { $_.Name -notin @('InfLink-rs.plugin','PluginMarket.plugin') })
            }
            foreach ($folder in @('plugins_dev','plugins_runtime')) {
                $extra = Join-Path $state.ProfileDir $folder
                if ((Test-Path -LiteralPath $extra) -and (Get-ChildItem -LiteralPath $extra -Directory)) { $sharedPlugins += $extra }
            }
            foreach ($file in $state.Files) {
                Assert-PlainPath $file.Path
                if ($sharedPlugins.Count -gt 0 -and [IO.Path]::GetFileName($file.Path) -eq 'msimg32.dll') {
                    Write-Warning '已有其他插件使用 BetterNCM，保留共享框架。'
                    continue
                }
                if ((Test-Path -LiteralPath $file.Path) -and (Get-Hash $file.Path) -eq $file.Hash) {
                    Remove-Item -LiteralPath $file.Path
                } elseif (Test-Path -LiteralPath $file.Path) {
                    Write-Warning ('保留已更改文件：' + $file.Path)
                }
            }
            Remove-Item -LiteralPath $statePath
        }
        exit 0
    }
    Assert-PlainPath $CloudMusicDir
    Assert-PlainPath $ProfileDir
    $CloudMusicDir = [IO.Path]::GetFullPath($CloudMusicDir).TrimEnd('\','/')
    $ProfileDir = [IO.Path]::GetFullPath($ProfileDir).TrimEnd('\','/')
    $player = Join-Path $CloudMusicDir 'cloudmusic.exe'
    if (-not (Test-Path -LiteralPath $player)) { throw '所选目录没有 cloudmusic.exe。' }
    if ((Get-Item -LiteralPath $player).VersionInfo.FileVersion -ne '3.1.41.205564') { throw '当前仅验证网易云 3.1.41.205564，未修改此客户端。' }
    $stream = [IO.File]::OpenRead($player)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw '网易云可执行文件无效。' }
        $stream.Position = 0x3c
        $pe = $reader.ReadInt32()
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw '此安装包仅支持 x64 网易云。' }
    } finally { $stream.Dispose() }
    $specs = @(
        @{ Source='BetterNCMII-1.3.4.dll'; Path=(Join-Path $CloudMusicDir 'msimg32.dll'); Hash='a7c77af418d7940e63faa58ea036fba1f4baad497947109ea52ed78c8e86608f' },
        @{ Source='InfLink-rs-3.4.0.plugin'; Path=(Join-Path $ProfileDir 'plugins/InfLink-rs.plugin'); Hash='7d0b7b863b072a99b88eb614c788099315e690920889aaca22685d3663661468' }
    )
    if ($state -and ($state.CloudMusicDir -ne $CloudMusicDir -or $state.ProfileDir -ne $ProfileDir)) { throw '组件已安装在另一目录，请先卸载原有组件。' }
    foreach ($spec in $specs) {
        Assert-PlainPath $spec.Path
        if ((Get-Hash (Join-Path $PayloadDir $spec.Source)) -ne $spec.Hash) { throw ('安装包组件校验失败：' + $spec.Source) }
        if ((Test-Path -LiteralPath $spec.Path) -and (Get-Hash $spec.Path) -ne $spec.Hash) { throw ('已有不同版本或未知文件，未覆盖：' + $spec.Path) }
    }
    # Reject duplicate copies under other names, including development plugins.
    foreach ($folder in @('plugins','plugins_dev')) {
        $root = Join-Path $ProfileDir $folder
        if (Test-Path -LiteralPath $root) {
            foreach ($item in Get-ChildItem -LiteralPath $root -Recurse -File) {
                if ($item.FullName -eq $specs[1].Path) { continue }
                if ($item.Name -eq 'manifest.json') {
                    $manifest = Get-Content -LiteralPath $item.FullName -Raw | ConvertFrom-Json
                    if ($manifest.name -eq 'InfLink-rs' -or $manifest.name -eq 'InfLink') { throw '发现其他 InfLink 插件，请先在原插件管理器中处理，安装未修改文件。' }
                } elseif ($item.Extension -eq '.plugin') {
                    Add-Type -AssemblyName System.IO.Compression.FileSystem
                    $zip = [IO.Compression.ZipFile]::OpenRead($item.FullName)
                    try {
                        $entry = $zip.GetEntry('manifest.json')
                        if ($entry) {
                            $sr = New-Object IO.StreamReader($entry.Open())
                            try { $manifest = $sr.ReadToEnd() | ConvertFrom-Json } finally { $sr.Dispose() }
                            if ($manifest.name -in @('InfLink-rs','InfLink')) { throw '发现其他 InfLink 插件，安装未修改文件。' }
                        }
                    } finally { $zip.Dispose() }
                }
            }
        }
    }
    $runtimeMissing = @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll') | Where-Object { -not (Test-Path (Join-Path $env:WINDIR ('System32/' + $_))) }
    if ($runtimeMissing) {
        $runtime = Join-Path $PayloadDir 'vc_redist.x64.exe'
        if ((Get-Hash $runtime) -ne 'cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b') { throw 'VC++ 运行库校验失败。' }
        $result = Start-Process -FilePath $runtime -ArgumentList '/install /passive /norestart' -WindowStyle Hidden -Wait -PassThru
        if ($result.ExitCode -notin @(0,1638,3010)) { throw ('VC++ 运行库安装失败：' + $result.ExitCode) }
        if ($result.ExitCode -eq 3010) { throw 'VC++ 运行库需要重启 Windows。重启后请重新运行本安装包。' }
        foreach ($dll in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')) {
            if (-not (Test-Path (Join-Path $env:WINDIR ('System32/' + $dll)))) { throw ('VC++ 运行库仍缺少：' + $dll) }
        }
    }
    Assert-PlayerClosed
    $owned = @()
    if ($state) { $owned = @($state.Files) }
    $created = @()
    try {
        # Persist a journal before each new file, so interrupted installs are recoverable.
        foreach ($spec in $specs) {
            if (-not (Test-Path -LiteralPath $spec.Path)) {
                $owned += @{ Path=$spec.Path; Hash=$spec.Hash }
                Write-State @{ CloudMusicDir=$CloudMusicDir; ProfileDir=$ProfileDir; Files=$owned }
                New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($spec.Path)) -Force | Out-Null
                $source = [IO.File]::OpenRead((Join-Path $PayloadDir $spec.Source))
                try {
                    $dest = [IO.File]::Open($spec.Path,[IO.FileMode]::CreateNew)
                    $created += $spec.Path
                    try { $source.CopyTo($dest) } finally { $dest.Dispose() }
                } finally { $source.Dispose() }
                if ((Get-Hash $spec.Path) -ne $spec.Hash) { throw '组件写入后校验失败。' }
            }
        }
    } catch {
        foreach ($path in $created) { Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue }
        if ($state) { Write-State $state } elseif (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath }
        throw
    }
    Write-Output '网易云同步组件已安装，请重新启动网易云。'
    exit 0
} catch {
    New-Item -ItemType Directory -Path $StateDir -Force | Out-Null
    $_.Exception.Message | Set-Content -LiteralPath (Join-Path $StateDir 'component-error.txt') -Encoding UTF8
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}
