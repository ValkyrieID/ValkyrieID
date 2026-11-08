# Runs the OWASP ZAP scans against a throwaway ValkyrieID in Docker (Windows version of run.sh; works with Podman
# behind the Docker CLI). See run.sh and docs/security/zap.md.
#
# Usage: security/zap/run.ps1 [-Image <image>] [-Scan baseline,api,login,admin] [-Keep]
param(
    [string]$Image,
    [ValidateSet('baseline', 'api', 'login', 'admin')][string[]]$Scan = @('baseline', 'api', 'login', 'admin'),
    [switch]$Keep
)

# Native commands write progress to stderr; their exit codes are checked instead.
$ErrorActionPreference = 'Continue'
$dir = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $dir)
$out = Join-Path $dir 'out'
$target = 'https://valkyrieid.localtest.me:9443'

# `docker compose`, or the standalone docker-compose (e.g. with Podman).
$composeExe = 'docker-compose'
$compose = @('-f', (Join-Path $dir 'docker-compose.yml'))
docker compose version *> $null
if ($LASTEXITCODE -eq 0) { $composeExe = 'docker'; $compose = @('compose') + $compose }

function New-RandomString([int]$bytes) {
    # Works on Windows PowerShell 5.1 and PowerShell 7.
    $buffer = New-Object byte[] $bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($buffer)
    $rng.Dispose()
    [Convert]::ToBase64String($buffer)
}

if (-not $Image) {
    $Image = 'valkyrieid:zap'
    docker build -t $Image $root
    if ($LASTEXITCODE -ne 0) { Write-Error 'Building the image failed.'; exit 1 }
}

$env:VALKYRIEID_IMAGE = $Image
$env:VALKYRIEID_MASTER_KEY = New-RandomString 32
$env:ZAP_ADMIN_PASSWORD = New-RandomString 24

$exitCode = 1
try {
    & $composeExe @compose --profile zap down -v --remove-orphans *> $null
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Copy-Item (Join-Path $root 'docs/public/openapi/admin-api.json') (Join-Path $out 'admin-api.json')
    & $composeExe @compose up -d --wait valkyrieid
    if ($LASTEXITCODE -ne 0) { throw 'Starting ValkyrieID failed.' }

    $cli = $compose + @('exec', '-T', 'valkyrieid', 'dotnet', '/app/valkyrieid.dll')
    $env:ZAP_ADMIN_PASSWORD | & $composeExe @cli user create zapadmin --email zapadmin@example.test | Out-Null
    & $composeExe @cli admin grant-admin zapadmin | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Creating the admin failed.' }
    # A second admin that never enrolls an authenticator, for the enrollment page (pending-login.js).
    $env:ZAP_ADMIN_PASSWORD | & $composeExe @cli user create zapenroll --email zapenroll@example.test | Out-Null
    & $composeExe @cli admin grant-admin zapenroll | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Creating the admin failed.' }

    $zap = $compose + @('run', '--rm', 'zap')
    $reports = @()
    foreach ($name in $Scan) {
        switch ($name) {
            # -I: warnings don't fail the scan; check_alerts.py decides.
            'baseline' { & $composeExe @zap zap-baseline.py -t "$target/" -I -J out/baseline.json -r out/baseline.html; $reports += 'baseline.json' }
            'api' { & $composeExe @zap zap-api-scan.py -t /zap/wrk/out/admin-api.json -f openapi -O $target -I -J out/api.json -r out/api.html; $reports += 'api.json' }
            'login' {
                & $composeExe @zap zap.sh -cmd -autorun /zap/wrk/login-pages.yaml `
                    -config 'anticsrf.tokens.token(0).name=__RequestVerificationToken' -config 'anticsrf.tokens.token(0).enabled=true'
                $reports += 'login-pages.json'
            }
            'admin' { & $composeExe @zap zap.sh -cmd -autorun /zap/wrk/admin-api.yaml; $reports += 'admin-api-auth.json' }
        }
    }

    python (Join-Path $dir 'check_alerts.py') @($reports | ForEach-Object { Join-Path $out $_ })
    $exitCode = $LASTEXITCODE
}
finally {
    & $composeExe @compose logs --no-color valkyrieid *> (Join-Path $out 'valkyrieid.log')
    if (-not $Keep) { & $composeExe @compose --profile zap down -v --remove-orphans *> $null }
    # Secrets of the throwaway admin.
    Remove-Item (Join-Path $out 'admin-token-state.json*') -ErrorAction SilentlyContinue
    Remove-Item Env:VALKYRIEID_MASTER_KEY, Env:ZAP_ADMIN_PASSWORD
}
exit $exitCode
