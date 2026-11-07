# Measures the container footprint against the targets in PROJECT.md §11: image size, startup to ready (SQLite,
# fresh volume), and idle memory after a settle period. Exits with 1 when a target is exceeded.
# See docs/guide/docker.md#footprint.
#
# Usage: footprint/measure.ps1 [-Image <name>] [-SkipBuild] [-SettleSeconds 30] [-Port 18080]
#                              [-MaxImageMB 210] [-MaxMemoryMB 150] [-MaxStartupSeconds 4]
# Requires Docker (or Podman with the docker CLI).
param(
    [string]$Image = 'valkyrieid:footprint',
    [switch]$SkipBuild,
    [int]$SettleSeconds = 30,
    [int]$Port = 18080,
    [double]$MaxImageMB = 210,
    [double]$MaxMemoryMB = 150,
    [double]$MaxStartupSeconds = 4
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$container = "valkyrieid-footprint-$PID"
$volume = "valkyrieid-footprint-$PID"

if (-not $SkipBuild) {
    docker build -t $Image $root
    if ($LASTEXITCODE -ne 0) { throw 'Building the image failed.' }
}

# Works on Windows PowerShell 5.1 and PowerShell 7.
$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes)
$rng.Dispose()
$masterKey = [Convert]::ToBase64String($bytes)

function ConvertTo-MB([string]$value) {
    if ($value -notmatch '^\s*([\d.]+)\s*([KMGT]?i?B)') { throw "Unexpected size '$value'." }
    $number = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
    switch -CaseSensitive ($Matches[2]) {
        'B' { $number / 1MB }
        { $_ -in 'kB', 'KB', 'KiB' } { $number / 1KB }
        { $_ -in 'MB', 'MiB' } { $number }
        { $_ -in 'GB', 'GiB' } { $number * 1KB }
        default { throw "Unexpected unit in '$value'." }
    }
}

$imageMB = [double](docker image inspect $Image --format '{{.Size}}') / 1MB

docker volume create $volume | Out-Null
try {
    # The periodic health check is off while measuring: each probe starts a second dotnet process in the container,
    # whose memory docker stats would count. The command is run once explicitly below.
    docker create --name $container --no-healthcheck -p "127.0.0.1:${Port}:8080" -v "${volume}:/data" `
        -e Issuer__Url=https://id.example.test -e "MasterKey__Current=$masterKey" $Image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Creating the container failed.' }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    docker start $container | Out-Null
    $ready = $false
    while ($watch.Elapsed.TotalSeconds -lt 60) {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/health/ready" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        }
        catch { }
        Start-Sleep -Milliseconds 20
    }
    $startup = $watch.Elapsed.TotalSeconds
    if (-not $ready) {
        docker logs $container
        throw 'The server did not become ready within 60 seconds.'
    }

    # The image's own health check command, as the container runtime runs it (no shell in the image).
    docker exec $container dotnet /app/valkyrieid.dll healthcheck
    if ($LASTEXITCODE -ne 0) { throw 'valkyrieid healthcheck failed inside the container.' }

    Start-Sleep -Seconds $SettleSeconds
    $memory = ConvertTo-MB ((docker stats --no-stream --format '{{.MemUsage}}' $container) -split '/')[0]
}
finally {
    docker rm -f $container 2>&1 | Out-Null
    docker volume rm $volume 2>&1 | Out-Null
}

$results = @(
    [pscustomobject]@{ Metric = 'Image size (MB)'; Value = [math]::Round($imageMB, 1); Target = $MaxImageMB }
    [pscustomobject]@{ Metric = 'Startup to ready (s)'; Value = [math]::Round($startup, 2); Target = $MaxStartupSeconds }
    [pscustomobject]@{ Metric = 'Idle memory (MB)'; Value = [math]::Round($memory, 1); Target = $MaxMemoryMB }
)
$results | ForEach-Object { $_ | Add-Member Result ($(if ($_.Value -lt $_.Target) { 'pass' } else { 'FAIL' })) }
$results | Format-Table -AutoSize | Out-String | Write-Host

if ($results | Where-Object Result -eq 'FAIL') {
    Write-Host 'Footprint targets exceeded.'
    exit 1
}
