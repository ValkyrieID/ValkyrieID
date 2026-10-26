# Registers the test user and clients for the OpenID Foundation conformance suite (Basic OP and Config OP)
# and writes the plan configurations to conformance/out/. See docs/operations/conformance.md.
#
# Usage: conformance/setup.ps1 [-Suite <suite-base-url>] [-Alias <alias>]
# Requires the same configuration as the server (Database__*, MasterKey__Current, Issuer__Url).
param(
    [string]$Suite = 'https://www.certification.openid.net',
    [string]$Alias = 'valkyrieid'
)

$ErrorActionPreference = 'Stop'
if (-not $env:Issuer__Url) { throw 'Set Issuer__Url to the public issuer URL of the server under test.' }

$issuer = $env:Issuer__Url.TrimEnd('/')
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'conformance/out'
$callback = "$($Suite.TrimEnd('/'))/test/a/$Alias/callback"
$project = Join-Path $root 'src/ValkyrieID.Server'

New-Item -ItemType Directory -Force -Path $out | Out-Null
# Works on Windows PowerShell 5.1 and PowerShell 7.
$bytes = New-Object byte[] 18
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
$password = [Convert]::ToBase64String($bytes)

$password | dotnet run --project $project --no-launch-profile -- user create conformance --email conformance@example.test --name 'Conformance Tester'
if ($LASTEXITCODE -ne 0) { throw 'Creating the test user failed.' }

function New-ConformanceClient([string]$clientId) {
    $output = dotnet run --project $project --no-launch-profile -- client create $clientId --grant authorization_code --redirect-uri $callback `
        --scope openid --scope profile --scope email --scope roles --scope groups --no-pkce
    if ($LASTEXITCODE -ne 0) { throw "Creating client $clientId failed." }
    ($output | Where-Object { $_ -like 'client_secret: *' }) -replace '^client_secret: ', ''
}

$secret1 = New-ConformanceClient 'conformance-1'
$secret2 = New-ConformanceClient 'conformance-2'
$discovery = "$issuer/.well-known/openid-configuration"

[ordered]@{
    alias       = $Alias
    description = 'ValkyrieID Basic OP'
    server      = @{ discoveryUrl = $discovery }
    client      = [ordered]@{ client_id = 'conformance-1'; client_secret = $secret1 }
    client2     = [ordered]@{ client_id = 'conformance-2'; client_secret = $secret2 }
} | ConvertTo-Json | Set-Content (Join-Path $out 'basic-op.json')

[ordered]@{
    alias       = "$Alias-config"
    description = 'ValkyrieID Config OP'
    server      = @{ discoveryUrl = $discovery }
} | ConvertTo-Json | Set-Content (Join-Path $out 'config-op.json')

Write-Host "Wrote $out/basic-op.json and $out/config-op.json."
Write-Host "Test user: conformance  password: $password"
Write-Host 'The files contain client secrets; delete conformance/out/ when you''re done.'
