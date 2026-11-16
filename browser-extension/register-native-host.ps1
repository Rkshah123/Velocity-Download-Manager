param(
    [Parameter(Mandatory = $true)][string]$HostExecutable,
    [Parameter(Mandatory = $true)][string]$ChromiumExtensionId
)

# Chrome and Microsoft Edge only. Both are Chromium browsers and share the same native-messaging manifest.

$resolvedHost = (Resolve-Path -LiteralPath $HostExecutable).Path
$manifestDirectory = Join-Path $env:LOCALAPPDATA "Velocity\native-host"
New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null

$manifest = @{
    name = "com.velocity.download_manager"
    description = "Velocity native messaging host"
    path = $resolvedHost
    type = "stdio"
    allowed_origins = @("chrome-extension://$ChromiumExtensionId/")
} | ConvertTo-Json

$manifestPath = Join-Path $manifestDirectory "com.velocity.download_manager.json"
$manifest | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$browserKeys = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.velocity.download_manager",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.velocity.download_manager"
)
foreach ($key in $browserKeys) {
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name '(default)' -Value $manifestPath
}

Write-Output "Registered the Velocity native messaging host for Google Chrome and Microsoft Edge."
Write-Output "Manifest: $manifestPath"
Write-Output "Allowed extension: chrome-extension://$ChromiumExtensionId/"
