$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'scripts\UnifiedInstallerLifecycle.Common.ps1')
$count = 0
function Assert-Throws([scriptblock]$Action, [string]$Expected) {
    $caught = $false
    try { & $Action } catch { if ($_.Exception.Message -cne $Expected) { throw }; $caught = $true }
    if (-not $caught) { throw "Expected $Expected" }
    $script:count++
}
function New-ValidPlan {
    return [ordered]@{
        schemaVersion = 1; kind = 'yuanshu-unified-installer-lifecycle'; oldVersion = '0.7.0'; newVersion = '0.8.0'
        oldInstaller = [ordered]@{ fileName = 'old-installer.exe'; sha256 = ('A' * 64) }
        newInstaller = [ordered]@{ fileName = 'unified-installer.exe'; sha256 = ('B' * 64) }
        installerExecutions = 5
        payload = @('ScreenGuide.DesktopClient.exe', 'ScreenGuide.DesktopHost.exe', 'island/yuanshu-island.exe') |
            ForEach-Object { [ordered]@{ path = $_; sha256 = ('C' * 64) } }
    }
}
$plan = New-ValidPlan
Assert-UnifiedLifecyclePlan $plan
$count++
foreach ($path in @('../escape.exe', 'C:/escape.exe', '/escape.exe', 'island/../escape.exe', 'island\escape.exe', 'a//b', 'a/./b', 'a:stream')) {
    Assert-Throws { Assert-UnifiedPayloadRelativePath $path } 'unified_lifecycle_payload_path_invalid'
}
Assert-Throws { Assert-UnifiedOwnedRoot 'C:\Windows\YuanshuUnifiedLifecycle-test' } 'unified_lifecycle_output_path_invalid'
Assert-Throws { Assert-UnifiedOwnedRoot (Join-Path ([IO.Path]::GetTempPath()) 'OtherProduct-test') } 'unified_lifecycle_output_path_invalid'
$plan = New-ValidPlan
$plan.payload += [ordered]@{ path = 'SCREENGUIDE.DESKTOPCLIENT.EXE'; sha256 = ('D' * 64) }
Assert-Throws { Assert-UnifiedLifecyclePlan $plan } 'unified_lifecycle_payload_invalid'
$plan = New-ValidPlan
$plan.payload[2].path = 'island/renamed.exe'
Assert-Throws { Assert-UnifiedLifecyclePlan $plan } 'unified_lifecycle_required_component_missing'
$plan = New-ValidPlan
$plan.payload[0].sha256 = 'invalid'
Assert-Throws { Assert-UnifiedLifecyclePlan $plan } 'unified_lifecycle_payload_invalid'
$plan = New-ValidPlan
$plan.installerExecutions = 6
Assert-Throws { Assert-UnifiedLifecyclePlan $plan } 'unified_lifecycle_plan_invalid'
$plan = New-ValidPlan
$plan.newInstaller.fileName = '../../host.exe'
Assert-Throws { Assert-UnifiedLifecyclePlan $plan } 'unified_lifecycle_plan_invalid'
$plan = New-ValidPlan
$plan.webView2Installer = [ordered]@{ fileName = 'webview2-offline-x64.exe'; sha256 = ('D' * 64); signatureStatus = 'Valid'; signerSubject = 'CN=Microsoft Corporation, O=Microsoft Corporation, C=US' }
Assert-UnifiedLifecyclePlan $plan
$count++
$plan.webView2Installer.signatureStatus = 'NotSigned'
Assert-Throws { Assert-UnifiedLifecyclePlan $plan } 'unified_lifecycle_webview2_dependency_invalid'
$plan.webView2Installer.signatureStatus = 'Valid'
$plan.webView2Installer.signerSubject = 'CN=Fake, O=Fake Corporation, C=US'
Assert-Throws { Assert-UnifiedLifecyclePlan $plan } 'unified_lifecycle_webview2_dependency_invalid'
Assert-Throws { & (Join-Path $repoRoot 'scripts\Invoke-UnifiedSandboxLifecycle.ps1') } 'unified_lifecycle_requires_windows_sandbox'
foreach ($scriptName in @('UnifiedInstallerLifecycle.Common.ps1', 'New-UnifiedSandboxLifecycle.ps1', 'Invoke-UnifiedSandboxLifecycle.ps1')) {
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot ('scripts\' + $scriptName)), [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw ($errors | Out-String) }
    $count++
}
$installer = Get-Content -LiteralPath (Join-Path $repoRoot 'installer\ScreenGuideDesktop.iss') -Raw
$icons = [regex]::Match($installer, '(?ms)^\[Icons\]\r?\n(?<section>.*?)(?=^\[)').Groups['section'].Value
foreach ($entry in @('{group}\元枢', '{userdesktop}\元枢')) {
    $line = @($icons -split '\r?\n' | Where-Object { $_.StartsWith('Name: "' + $entry + '";') })
    if ($line.Count -ne 1 -or $line[0] -notmatch 'Parameters: "--assistant";') {
        throw 'unified_installer_manual_entry_must_request_voice'
    }
    $count++
}
$run = [regex]::Match($installer, '(?ms)^\[Run\]\r?\n(?<section>.*?)(?=^\[)').Groups['section'].Value
if ($run -notmatch 'Parameters: "--paused";' -or $run -match 'Parameters: "--assistant"') {
    throw 'unified_installer_postinstall_must_not_request_voice'
}
$count++
[ordered]@{ passed = $true; checks = $count; nativeInstallerExecutions = 0; scope = 'contract-and-host-refusal-only' } | ConvertTo-Json -Compress
