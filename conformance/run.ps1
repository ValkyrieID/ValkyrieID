# Runs the OpenID conformance plans against ValkyrieID in Docker: builds the image, starts the suite and ValkyrieID
# (docker-compose.yml), registers the test user and clients (setup.ps1 -Docker), runs the plans (run-plans.py), and
# removes everything again. Results and logs are written to conformance/out/results/. Works with Podman behind the
# Docker CLI. See docs/operations/conformance.md.
#
# Usage: conformance/run.ps1 [-Full] [-Image <image>] [-Keep]
#   -Full    also run the client_secret_post and form_post variants (release tags)
#   -Image   test an existing image instead of building one from this checkout
#   -Keep    leave the containers running afterwards (the suite stays at https://localhost.emobix.co.uk:8443)
param(
    [switch]$Full,
    [string]$Image,
    [switch]$Keep
)

# Native commands write progress to stderr; their exit codes are checked instead.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root 'conformance'
$out = Join-Path $dir 'out'

# `docker compose`, or the standalone docker-compose (e.g. with Podman).
$composeExe = 'docker-compose'
$compose = @('-f', (Join-Path $dir 'docker-compose.yml'))
docker compose version *> $null
if ($LASTEXITCODE -eq 0) { $composeExe = 'docker'; $compose = @('compose') + $compose }

if (-not $Image) {
    $Image = 'valkyrieid:conformance'
    docker build -t $Image $root
    if ($LASTEXITCODE -ne 0) { Write-Error 'Building the image failed.'; exit 1 }
}

# Works on Windows PowerShell 5.1 and PowerShell 7.
$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
$env:VALKYRIEID_MASTER_KEY = [Convert]::ToBase64String($bytes)
$env:VALKYRIEID_IMAGE = $Image
$env:PLANS = if ($Full) { 'full' } else { 'default' }

$exitCode = 1
try {
    & $composeExe @compose --profile runner down -v --remove-orphans *> $null
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    & $composeExe @compose up -d --wait valkyrieid nginx
    if ($LASTEXITCODE -ne 0) { throw 'Starting the containers failed.' }
    & (Join-Path $dir 'setup.ps1') -Docker
    & $composeExe @compose run --rm runner
    $exitCode = $LASTEXITCODE
}
finally {
    $results = Join-Path $out 'results'
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    & $composeExe @compose logs --no-color valkyrieid *> (Join-Path $results 'valkyrieid.log')
    & $composeExe @compose logs --no-color server *> (Join-Path $results 'suite.log')
    if (-not $Keep) { & $composeExe @compose --profile runner down -v --remove-orphans *> $null }
    Remove-Item Env:VALKYRIEID_MASTER_KEY
}
exit $exitCode
