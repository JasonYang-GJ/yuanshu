# This entrypoint is exclusively for a fresh Windows Sandbox. It cannot run on the developer host.
$ErrorActionPreference = 'Stop'
$inputRoot = 'C:\YuanshuUnifiedInput'
$evidenceRoot = 'C:\YuanshuUnifiedEvidence'
if ($env:USERNAME -cne 'WDAGUtilityAccount' -or
    [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -cne $inputRoot) {
    throw 'unified_lifecycle_requires_windows_sandbox'
}
. (Join-Path $inputRoot 'UnifiedInstallerLifecycle.Common.ps1')
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\YuanshuDesktop'
$dataRoot = Join-Path $env:LOCALAPPDATA 'ScreenGuide\V01'
$islandDataRoot = Join-Path $env:APPDATA 'com.yuanshu.island'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E2B9C242-2965-48BC-B2C6-CF83A2B11953}_is1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$productName = -join @([char]0x5143, [char]0x67A2)
$legacyCleanupName = -join @([char]0x5220, [char]0x9664, [char]0x5143, [char]0x67A2, [char]0x7528, [char]0x6237, [char]0x6570, [char]0x636E)
$resultPath = Join-Path $evidenceRoot 'lifecycle-result.json'
$phase = 'preflight'
$executions = [Collections.Generic.List[object]]::new()
$checks = [ordered]@{}
$hostExecutions = 0
$canaries = @()
$expectAutostart = $false

function Assert-Condition([bool]$Condition, [string]$Code) { if (-not $Condition) { throw $Code } }
function Assert-Hash([string]$Path, [string]$Expected) {
    Assert-Condition ((Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ceq $Expected) 'unified_lifecycle_hash_mismatch'
}
function Invoke-ObservedProcess([string]$File, [string[]]$Arguments, [string]$Kind) {
    # All arguments originate in this script. Quote each path independently for Windows PowerShell 5.1.
    $quoted = @($Arguments | ForEach-Object { '"' + $_ + '"' })
    $process = Start-Process -FilePath $File -ArgumentList $quoted -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(120000)) { throw 'unified_lifecycle_process_timeout' }
    $process.Refresh()
    $executions.Add([ordered]@{ phase = $script:phase; kind = $Kind; exitCode = $process.ExitCode })
    Assert-Condition ($process.ExitCode -eq 0) 'unified_lifecycle_process_failed'
}
function Install([string]$Installer) {
    Invoke-ObservedProcess $Installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/DIR=$installRoot", "/LOG=$(Join-Path $evidenceRoot ($script:phase + '.log'))") 'installer'
}
function Uninstall {
    Invoke-ObservedProcess (Join-Path $installRoot 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/LOG=$(Join-Path $evidenceRoot ($script:phase + '.log'))") 'uninstaller'
    # Inno's first process can exit before its relocated /SECONDPHASE child.
    # Wait for actual uninstall completion before inspecting data or reinstalling.
    $logPath = Join-Path $evidenceRoot ($script:phase + '.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $tail = if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 12 | Out-String } else { '' }
        $complete = $tail -match 'Uninstallation process succeeded\.' -and $tail -match 'Log closed\.' -and
            -not (Test-Path -LiteralPath (Join-Path $installRoot 'ScreenGuide.DesktopClient.exe')) -and
            -not (Test-Path -LiteralPath $uninstallKey)
        if ($complete) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert-Condition $complete 'unified_lifecycle_uninstall_completion_not_observed'
    $checks.uninstallSecondPhaseCompletionObserved = $true
}
function Invoke-RejectedInstaller([string]$Installer) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$installRoot",
        "/LOG=$(Join-Path $evidenceRoot ($script:phase + '.log'))") | ForEach-Object { '"' + $_ + '"' }
    $process = Start-Process -FilePath $Installer -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(60000)) { throw 'unified_lifecycle_rejection_timeout' }
    $process.Refresh()
    $executions.Add([ordered]@{ phase = $script:phase; kind = 'refused-installer'; exitCode = $process.ExitCode })
    Assert-Condition ($process.ExitCode -ne 0) 'unified_lifecycle_expected_refusal_missing'
}
function Invoke-HostOnce {
    $env:SCREEN_GUIDE_DATA_DIRECTORY = $dataRoot
    $env:SCREEN_GUIDE_PIPE_NAME = 'Yuanshu.UnifiedLifecycle.' + [guid]::NewGuid().ToString('N')
    Invoke-ObservedProcess (Join-Path $installRoot 'ScreenGuide.DesktopHost.exe') @('--run-once') 'host'
    $script:hostExecutions++
    Assert-Condition (Test-Path -LiteralPath (Join-Path $dataRoot 'state\tasking.db') -PathType Leaf) 'unified_lifecycle_database_missing'
}
function Assert-Canaries {
    foreach ($item in $script:canaries) { Assert-Hash $item.path $item.sha256 }
}
function Assert-Candidate {
    foreach ($file in @($plan.payload)) { Assert-Hash (Join-Path $installRoot ([string]$file.path)) ([string]$file.sha256) }
    foreach ($name in @('ScreenGuide.DesktopClient.exe', 'ScreenGuide.DesktopHost.exe')) {
        $version = ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $installRoot $name)).FileVersion).Trim()
        Assert-Condition ($version -ceq ($plan.newVersion + '.0')) 'unified_lifecycle_component_version_mismatch'
    }
    $registration = Get-ItemProperty -LiteralPath $uninstallKey
    Assert-Condition ($registration.DisplayVersion -ceq $plan.newVersion) 'unified_lifecycle_registration_mismatch'
    $registrations = @(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' |
        Where-Object { $_.DisplayName -match ($productName + '|Yuanshu Island') })
    Assert-Condition ($registrations.Count -eq 1) 'unified_lifecycle_multiple_product_registrations'
    $shell = New-Object -ComObject WScript.Shell
    $desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) ($productName + '.lnk')
    $menuRoot = Join-Path ([Environment]::GetFolderPath('Programs')) $productName
    foreach ($linkPath in @($desktopLink, (Join-Path $menuRoot ($productName + '.lnk')))) {
        Assert-Condition (Test-Path -LiteralPath $linkPath -PathType Leaf) 'unified_lifecycle_entry_missing'
        $link = $shell.CreateShortcut($linkPath)
        Assert-Condition ($link.TargetPath -ieq (Join-Path $installRoot 'ScreenGuide.DesktopClient.exe') -and
            $link.Arguments -ceq '--assistant') 'unified_lifecycle_entry_not_explicit_voice'
    }
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $menuRoot ($legacyCleanupName + '.lnk')))) 'unified_lifecycle_legacy_cleanup_entry_retained'
    $runValues = if (Test-Path -LiteralPath $runKey) { Get-ItemProperty -LiteralPath $runKey } else { $null }
    Assert-Condition ($null -eq $runValues -or
        ($runValues.PSObject.Properties.Name -notcontains 'Yuanshu' -and $runValues.PSObject.Properties.Name -notcontains 'Yuanshu Island')) 'unified_lifecycle_duplicate_autostart'
    if ($script:expectAutostart) {
        Assert-Condition ($runValues.ScreenGuideDesktop -ceq ('"' + (Join-Path $installRoot 'ScreenGuide.DesktopClient.exe') + '" --background')) 'unified_lifecycle_autostart_not_migrated'
    } else {
        Assert-Condition ($null -eq $runValues -or $runValues.PSObject.Properties.Name -notcontains 'ScreenGuideDesktop') 'unified_lifecycle_unrequested_autostart'
    }
}

try {
    $plan = Get-Content -Raw -LiteralPath (Join-Path $inputRoot 'plan.json') | ConvertFrom-Json
    Assert-UnifiedLifecyclePlan $plan
    Assert-Condition (-not (Test-Path -LiteralPath $installRoot) -and -not (Test-Path -LiteralPath $uninstallKey) -and
        -not (Test-Path -LiteralPath $dataRoot) -and -not (Test-Path -LiteralPath $islandDataRoot)) 'unified_lifecycle_sandbox_not_clean'
    $oldInstaller = Join-Path $inputRoot $plan.oldInstaller.fileName
    $newInstaller = Join-Path $inputRoot $plan.newInstaller.fileName
    Assert-Hash $oldInstaller $plan.oldInstaller.sha256
    Assert-Hash $newInstaller $plan.newInstaller.sha256
    $phase = 'webview2-preflight'
    $runtimeBefore = Get-UnifiedWebView2Version
    if (-not $runtimeBefore -and $null -ne $plan.webView2Installer) {
        $runtimeInstaller = Join-Path $inputRoot $plan.webView2Installer.fileName
        Assert-Hash $runtimeInstaller $plan.webView2Installer.sha256
        $runtimeSignature = Get-AuthenticodeSignature -LiteralPath $runtimeInstaller
        Assert-Condition ($runtimeSignature.Status -eq 'Valid' -and $runtimeSignature.SignerCertificate.Subject -ceq $plan.webView2Installer.signerSubject) 'unified_lifecycle_webview2_signature_invalid'
        Invoke-ObservedProcess $runtimeInstaller @('/silent', '/install') 'dependency'
    }
    $runtimeVersion = Get-UnifiedWebView2Version
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($runtimeVersion)) 'unified_lifecycle_webview2_missing'
    $checks.webView2Version = $runtimeVersion
    $checks.webView2Source = if ($runtimeBefore) { 'SANDBOX_PREINSTALLED' } else { 'OFFICIAL_SIGNED_OFFLINE_INSTALLER' }
    $phase = 'old-install'
    Install $oldInstaller
    Assert-Condition ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -ceq $plan.oldVersion) 'unified_lifecycle_old_version_mismatch'
    $phase = 'old-host-start'
    Invoke-HostOnce
    foreach ($directory in @((Join-Path $dataRoot 'settings'), $islandDataRoot)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $canary = Join-Path $directory 'unified-lifecycle-fake-data.json'
        [IO.File]::WriteAllText($canary, '{"fake":true,"value":"preserve-unified-upgrade-data"}', [Text.UTF8Encoding]::new($false))
        $canaries += [ordered]@{ path = $canary; sha256 = (Get-FileHash -LiteralPath $canary -Algorithm SHA256).Hash }
    }
    $database = Join-Path $dataRoot 'state\tasking.db'
    $databaseBefore = (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash
    # The verified legacy preference is synthetic and exists only inside this Sandbox.
    $legacyRoot = Join-Path $env:TEMP ('YuanshuUnifiedLegacy-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $legacyRoot | Out-Null
    $legacyExecutable = Join-Path $legacyRoot 'yuanshu-island.exe'
    Add-Type -TypeDefinition '[assembly:System.Reflection.AssemblyProduct("Yuanshu Island")][assembly:System.Reflection.AssemblyTitle("Yuanshu Island")] public class LegacyIslandFixture { public static void Main() {} }' -OutputAssembly $legacyExecutable -OutputType ConsoleApplication
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -LiteralPath $runKey -Name 'Yuanshu Island' -Value ('"' + $legacyExecutable + '" --autostart') -PropertyType String -Force | Out-Null
    $expectAutostart = $true
    $phase = 'candidate-upgrade'
    Install $newInstaller
    Assert-Candidate
    Assert-Canaries
    Assert-Hash $database $databaseBefore
    $checks.upgradePayloadAndEntries = $true
    $checks.upgradeDatabaseUntouched = $true
    $checks.existingIslandAutostartMigratedToMain = $true
    # Exercise both refusal paths only against synthetic Sandbox state.
    $phase = 'downgrade-refusal'
    $savedVersion = (Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion
    try {
        Set-ItemProperty -LiteralPath $uninstallKey -Name DisplayVersion -Value '99.0.0'
        Invoke-RejectedInstaller $newInstaller
        Assert-Condition ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -ceq '99.0.0') 'unified_lifecycle_rejected_downgrade_mutated_registration'
    }
    finally { Set-ItemProperty -LiteralPath $uninstallKey -Name DisplayVersion -Value $savedVersion }
    Assert-Candidate
    Assert-Hash $database $databaseBefore
    $checks.downgradeRefused = $true
    $phase = 'running-component-refusal'
    $dummyRoot = Join-Path $env:TEMP ('YuanshuUnifiedProcessGate-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dummyRoot | Out-Null
    $dummyPath = Join-Path $dummyRoot 'ScreenGuide.DesktopHost.exe'
    $dummyStop = Join-Path $dummyRoot 'stop.txt'
    Add-Type -TypeDefinition 'public class FakeRunningComponent { public static void Main(string[] args) { var until=System.DateTime.UtcNow.AddMinutes(2); while(!System.IO.File.Exists(args[0]) && System.DateTime.UtcNow<until) System.Threading.Thread.Sleep(100); } }' -OutputAssembly $dummyPath -OutputType ConsoleApplication
    $dummy = Start-Process -FilePath $dummyPath -ArgumentList @('"' + $dummyStop + '"') -PassThru -WindowStyle Hidden
    try {
        Assert-Condition (-not $dummy.HasExited) 'unified_lifecycle_fake_process_missing'
        Invoke-RejectedInstaller $newInstaller
        Assert-Condition (-not $dummy.HasExited) 'unified_lifecycle_installer_killed_running_component'
    }
    finally {
        [IO.File]::WriteAllText($dummyStop, 'stop-owned-test-process')
        [void]$dummy.WaitForExit(10000)
    }
    Assert-Candidate
    Assert-Hash $database $databaseBefore
    $checks.runningComponentRefusedWithoutKill = $true
    $phase = 'candidate-host-start'
    Invoke-HostOnce
    Assert-Canaries
    $databaseAfterStart = (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash
    $phase = 'candidate-uninstall'
    Uninstall
    $runAfterUninstall = Get-ItemProperty -LiteralPath $runKey
    Assert-Condition ($runAfterUninstall.PSObject.Properties.Name -notcontains 'ScreenGuideDesktop') 'unified_lifecycle_owned_autostart_not_removed'
    $expectAutostart = $false
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $installRoot 'ScreenGuide.DesktopClient.exe')) -and
        -not (Test-Path -LiteralPath (Join-Path $installRoot 'island\yuanshu-island.exe')) -and
        -not (Test-Path -LiteralPath $uninstallKey)) 'unified_lifecycle_uninstall_incomplete'
    Assert-Canaries
    Assert-Hash $database $databaseAfterStart
    $checks.uninstallPreservedData = $true
    $phase = 'candidate-reinstall'
    Install $newInstaller
    Assert-Candidate
    Assert-Canaries
    Assert-Hash $database $databaseAfterStart
    $phase = 'reinstalled-host-start'
    Invoke-HostOnce
    Assert-Canaries
    $checks.reinstallPreservedData = $true
    $databaseBeforeFinal = (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash
    $phase = 'final-uninstall'
    Uninstall
    Assert-Canaries
    Assert-Hash $database $databaseBeforeFinal
    Assert-Condition (-not (Test-Path -LiteralPath $installRoot) -and -not (Test-Path -LiteralPath $uninstallKey)) 'unified_lifecycle_final_cleanup_incomplete'
    $checks.finalUninstallProgramRemoved = $true
    $checks.reinstallDidNotEnableAutostart = $true
    $checks.microphoneNeverStarted = $true
    $checks.installedClientVisualAcceptance = 'NOT_RUN_BY_THIS_LIFECYCLE'
    $installerCount = @($executions | Where-Object { $_.kind -in @('installer', 'uninstaller') }).Count
    $refusalCount = @($executions | Where-Object { $_.kind -eq 'refused-installer' }).Count
    Assert-Condition ($installerCount -eq $plan.installerExecutions -and $hostExecutions -eq 3 -and $refusalCount -eq 2) 'unified_lifecycle_execution_count_mismatch'
    Write-UnifiedJson $resultPath ([ordered]@{ status = 'PASS'; schemaVersion = 1; phase = 'complete'; checks = $checks;
        installerExecutions = $installerCount; refusedInstallerAttempts = $refusalCount; hostExecutions = $hostExecutions; executions = $executions.ToArray();
        oldInstallerSha256 = $plan.oldInstaller.sha256; newInstallerSha256 = $plan.newInstaller.sha256;
        payloadCount = @($plan.payload).Count; realProviderRequests = 0; userDataSource = 'SYNTHETIC_SANDBOX_ONLY' })
}
catch {
    $code = if ($_.Exception.Message -cmatch '^unified_lifecycle_[a-z0-9_]+$') { $_.Exception.Message } else { 'unified_lifecycle_unexpected_failure' }
    Write-UnifiedJson $resultPath ([ordered]@{ status = 'FAIL'; schemaVersion = 1; phase = $phase; errorCode = $code;
        exceptionType = $_.Exception.GetType().FullName; diagnostic = $_.Exception.Message;
        scriptLine = $_.InvocationInfo.ScriptLineNumber; checks = $checks; executions = $executions.ToArray() })
    exit 1
}
