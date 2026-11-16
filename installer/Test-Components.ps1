param([string]$FixtureExe = 'D:/Program/CloudMusic/cloudmusic.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$root = Join-Path $repo ('release/installer-tests/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$count = 0
function Assert($Condition, $Message) {
    if (-not $Condition) { throw $Message }
    $script:count++
}
function New-Fixture([string]$Name) {
    $path = Join-Path $root $Name
    New-Item -ItemType Directory -Path ($path + '/player'),($path + '/profile') -Force | Out-Null
    Copy-Item -LiteralPath $FixtureExe -Destination ($path + '/player/cloudmusic.exe')
    return $path
}
function Run-Component($Path, $Action = 'Install', $Payload = (Join-Path $repo 'release/installer-input'), [switch]$Running, [switch]$FailSecondWrite) {
    # A separate process contains the installer script's exit. Only process discovery is mocked.
    $body = "function Get-Process { " + $(if ($Running) { '"running"' } else { '$null' }) + " }; & '" +
        (Join-Path $PSScriptRoot 'Components.ps1').Replace("'","''") + "' -Action '" + $Action +
        "' -CloudMusicDir '" + $Path + "/player' -ProfileDir '" + $Path + "/profile' -StateDir '" +
        $Path + "/state' -PayloadDir '" + $Payload + "'"
    if ($FailSecondWrite) {
        $body = 'function New-Item { param($ItemType,$Path,[switch]$Force); if ($Path -like "*profile*plugins") { throw "injected second write failure" }; Microsoft.PowerShell.Management\New-Item @PSBoundParameters }; ' + $body
    }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded *> ($Path + '/output.txt')
    return $LASTEXITCODE
}
$fresh = New-Fixture 'fresh'
Assert ((Run-Component $fresh) -eq 0) 'Fresh install failed'
Assert (Test-Path ($fresh + '/player/msimg32.dll')) 'Framework missing'
Assert (Test-Path ($fresh + '/profile/plugins/InfLink-rs.plugin')) 'Plugin missing'
Assert ((Run-Component $fresh) -eq 0) 'Repeated install failed'
Assert ((Run-Component $fresh 'Uninstall') -eq 0) 'Uninstall failed'
Assert (-not (Test-Path ($fresh + '/player/msimg32.dll'))) 'Owned framework not removed'
Assert (-not (Test-Path ($fresh + '/profile/plugins/InfLink-rs.plugin'))) 'Owned plugin not removed'
$unknown = New-Fixture 'unknown'
Set-Content ($unknown + '/player/msimg32.dll') 'existing'
Assert ((Run-Component $unknown) -ne 0) 'Unknown DLL was overwritten'
Assert ((Get-Content ($unknown + '/player/msimg32.dll')) -eq 'existing') 'Unknown DLL changed'
$existing = New-Fixture 'existing'
Copy-Item (Join-Path $repo 'release/installer-input/BetterNCMII-1.3.4.dll') ($existing + '/player/msimg32.dll')
Assert ((Run-Component $existing) -eq 0) 'Existing verified framework rejected'
Assert ((Run-Component $existing 'Uninstall') -eq 0) 'Existing uninstall failed'
Assert (Test-Path ($existing + '/player/msimg32.dll')) 'Preexisting framework removed'
$modified = New-Fixture 'modified'
Assert ((Run-Component $modified) -eq 0) 'Modified fixture install failed'
Set-Content ($modified + '/profile/plugins/InfLink-rs.plugin') 'user modified'
Assert ((Run-Component $modified 'Uninstall') -eq 0) 'Modified uninstall failed'
Assert ((Get-Content ($modified + '/profile/plugins/InfLink-rs.plugin')) -eq 'user modified') 'Modified file removed'
$busy = New-Fixture 'busy'
Assert ((Run-Component $busy -Running) -ne 0) 'Running player not blocked'
Assert (-not (Test-Path ($busy + '/player/msimg32.dll'))) 'Busy player modified'
$failure = New-Fixture 'rollback'
Assert ((Run-Component $failure -FailSecondWrite) -ne 0) 'Write failure not reported'
Assert (-not (Test-Path ($failure + '/player/msimg32.dll'))) 'Partial install not rolled back'
Assert (-not (Test-Path ($failure + '/state/netease-components.json'))) 'Rollback journal retained'
$shared = New-Fixture 'shared'
Assert ((Run-Component $shared) -eq 0) 'Shared fixture install failed'
Set-Content ($shared + '/profile/plugins/Other.plugin') 'other plugin'
Assert ((Run-Component $shared 'Uninstall') -eq 0) 'Shared uninstall failed'
Assert (Test-Path ($shared + '/player/msimg32.dll')) 'Shared framework removed'
Assert (Test-Path ($shared + '/profile/plugins/Other.plugin')) 'Other plugin removed'
$wrong = New-Fixture 'wrong-version'
Copy-Item (Join-Path $env:WINDIR 'System32/notepad.exe') ($wrong + '/player/cloudmusic.exe') -Force
Assert ((Run-Component $wrong) -ne 0) 'Unknown version accepted'
Assert (-not (Test-Path ($wrong + '/player/msimg32.dll'))) 'Unknown version modified'
$tampered = New-Fixture 'tampered-payload'
New-Item -ItemType Directory -Path ($tampered + '/payload') | Out-Null
Copy-Item (Join-Path $repo 'release/installer-input/BetterNCMII-1.3.4.dll') ($tampered + '/payload')
Set-Content ($tampered + '/payload/InfLink-rs-3.4.0.plugin') 'corrupt'
Assert ((Run-Component $tampered -Payload ($tampered + '/payload')) -ne 0) 'Corrupt payload accepted'
Assert (-not (Test-Path ($tampered + '/player/msimg32.dll'))) 'Payload validation wrote files'
Write-Output "PASS: $count isolated installer assertions; fixtures: $root"
