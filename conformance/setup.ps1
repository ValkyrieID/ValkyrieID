# Registers the test user and clients for the OpenID Foundation conformance suite (Basic OP, Config OP,
# RP-Initiated Logout, and Back-Channel Logout) and writes the plan configurations to conformance/out/.
# See docs/operations/conformance.md.
#
# Usage: conformance/setup.ps1 [-Suite <suite-base-url>] [-Alias <alias>]
#        conformance/setup.ps1 -Docker
# Without -Docker, requires the same configuration as the server (Database__*, MasterKey__Current, Issuer__Url).
# With -Docker, runs the CLI in the valkyrieid service of conformance/docker-compose.yml, which must be running,
# and uses the suite in the same Compose project (run.ps1 does this).
param(
    [string]$Suite,
    [string]$Alias = 'valkyrieid',
    [switch]$Docker
)

$ErrorActionPreference = 'Stop'
# `docker compose`, or the standalone docker-compose (e.g. with Podman).
$composeExe = 'docker-compose'
$composeArgs = @()
try {
    docker compose version *> $null
    if ($LASTEXITCODE -eq 0) { $composeExe = 'docker'; $composeArgs = @('compose') }
}
catch { }
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'conformance/out'
$project = Join-Path $root 'src/ValkyrieID.Server'
$compose = Join-Path $root 'conformance/docker-compose.yml'

if ($Docker) {
    if (-not $Suite) { $Suite = 'https://localhost.emobix.co.uk:8443' }
    $issuer = 'https://valkyrieid.localtest.me:9443'
}
else {
    if (-not $Suite) { $Suite = 'https://www.certification.openid.net' }
    if (-not $env:Issuer__Url) { throw 'Set Issuer__Url to the public issuer URL of the server under test.' }
    $issuer = $env:Issuer__Url.TrimEnd('/')
}

$callback = "$($Suite.TrimEnd('/'))/test/a/$Alias/callback"
$postLogoutRedirect = "$($Suite.TrimEnd('/'))/test/a/$Alias/post_logout_redirect"
$backChannelLogout = "$($Suite.TrimEnd('/'))/test/a/$Alias/backchannel_logout"

function Invoke-Cli([string[]]$arguments, [string]$stdin) {
    # Native commands' stderr (e.g. Compose progress) isn't an error; the exit code is checked below.
    $ErrorActionPreference = 'Continue'
    if ($Docker) {
        $command = $composeArgs + @('-f', $compose, 'exec', '-T', 'valkyrieid', 'dotnet', '/app/valkyrieid.dll') + $arguments
        if ($stdin) { $result = $stdin | & $composeExe @command } else { $result = & $composeExe @command }
    }
    else {
        $command = @('run', '--project', $project, '--no-launch-profile', '--') + $arguments
        if ($stdin) { $result = $stdin | dotnet @command } else { $result = dotnet @command }
    }
    if ($LASTEXITCODE -ne 0) { throw "valkyrieid $($arguments -join ' ') failed." }
    $result
}

New-Item -ItemType Directory -Force -Path $out | Out-Null
# Works on Windows PowerShell 5.1 and PowerShell 7.
$bytes = New-Object byte[] 18
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
$password = [Convert]::ToBase64String($bytes)

Invoke-Cli @('user', 'create', 'conformance', '--email', 'conformance@example.test', '--name', 'Conformance Tester') $password | Out-Host

function New-ConformanceClient([string]$clientId, [string[]]$extra = @()) {
    $output = Invoke-Cli (@('client', 'create', $clientId, '--grant', 'authorization_code', '--grant', 'refresh_token', '--redirect-uri', $callback,
            '--scope', 'openid', '--scope', 'profile', '--scope', 'email', '--scope', 'roles', '--scope', 'groups', '--scope', 'offline_access',
            '--no-pkce', '--allow-secret-post') + $extra)
    (($output | Where-Object { $_ -like 'client_secret: *' }) -replace '^client_secret: ', '').Trim()
}

$secret1 = New-ConformanceClient 'conformance-1'
$secret2 = New-ConformanceClient 'conformance-2'
# The logout plans use their own clients: only the Back-Channel Logout plan's clients have a back-channel logout URI,
# because the other plans fail on unexpected logout tokens.
$rpLogoutSecret1 = New-ConformanceClient 'conformance-rp-logout-1' @('--post-logout-redirect-uri', $postLogoutRedirect)
$rpLogoutSecret2 = New-ConformanceClient 'conformance-rp-logout-2' @('--post-logout-redirect-uri', $postLogoutRedirect)
$logoutOptions = @('--post-logout-redirect-uri', $postLogoutRedirect, '--backchannel-logout-uri', $backChannelLogout)
$logoutSecret1 = New-ConformanceClient 'conformance-logout-1' $logoutOptions
$logoutSecret2 = New-ConformanceClient 'conformance-logout-2' $logoutOptions
$discovery = "$issuer/.well-known/openid-configuration"

[ordered]@{
    alias       = $Alias
    description = 'ValkyrieID Basic OP'
    server      = @{ discoveryUrl = $discovery }
    client      = [ordered]@{ client_id = 'conformance-1'; client_secret = $secret1 }
    client2     = [ordered]@{ client_id = 'conformance-2'; client_secret = $secret2 }
    # Used by oidcc-server-client-secret-post.
    client_secret_post = [ordered]@{ client_id = 'conformance-1'; client_secret = $secret1 }
} | ConvertTo-Json | Set-Content (Join-Path $out 'basic-op.json')

[ordered]@{
    alias       = "$Alias-config"
    description = 'ValkyrieID Config OP'
    server      = @{ discoveryUrl = $discovery }
} | ConvertTo-Json | Set-Content (Join-Path $out 'config-op.json')

foreach ($plan in @(
        @{ File = 'rp-initiated-logout.json'; Description = 'ValkyrieID RP-Initiated Logout'; Client = 'conformance-rp-logout'; Secrets = @($rpLogoutSecret1, $rpLogoutSecret2) },
        @{ File = 'backchannel-logout.json'; Description = 'ValkyrieID Back-Channel Logout'; Client = 'conformance-logout'; Secrets = @($logoutSecret1, $logoutSecret2) })) {
    [ordered]@{
        alias       = $Alias
        description = $plan.Description
        server      = @{ discoveryUrl = $discovery }
        client      = [ordered]@{ client_id = "$($plan.Client)-1"; client_secret = $plan.Secrets[0] }
        client2     = [ordered]@{ client_id = "$($plan.Client)-2"; client_secret = $plan.Secrets[1] }
    } | ConvertTo-Json | Set-Content (Join-Path $out $plan.File)
}

if ($Docker) {
    # Read by run-plans.py to fill in the browser automation (browser.json).
    [ordered]@{ username = 'conformance'; password = $password } | ConvertTo-Json | Set-Content (Join-Path $out 'test-user.json')
}

Write-Host "Wrote basic-op.json, config-op.json, rp-initiated-logout.json, and backchannel-logout.json to $out."
if (-not $Docker) { Write-Host "Test user: conformance  password: $password" }
Write-Host 'The files contain client secrets; delete conformance/out/ when you''re done.'
