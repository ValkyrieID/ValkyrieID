# Runs a throwaway ValkyrieID for a manual OAuch (https://oauch.io) test run behind a TLS-terminating proxy, and
# registers the test user and clients. See docs/security/oauch.md.
#
# Usage:
#   security/oauch/run.ps1 discover                       # shows the address the proxy connects from
#   security/oauch/run.ps1 start -KnownProxy <ip> [-Issuer https://dev.thuriot.be] [-Port 5102]
#   security/oauch/run.ps1 restart                        # recreates the container from a rebuilt image, same data
#   security/oauch/run.ps1 stop
#
# The server listens on plain HTTP; the proxy must send X-Forwarded-Proto: https. Only the proxy's address is trusted.
param(
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('discover', 'start', 'restart', 'stop')][string]$Command,
    [string]$KnownProxy,
    [string]$Issuer = 'https://dev.thuriot.be',
    [int]$Port = 5102,
    [string]$Image = 'ghcr.io/valkyrieid/valkyrieid'
)

$ErrorActionPreference = 'Stop'
$name = 'valkyrieid-oauch'
$volume = 'valkyrieid-oauch-data'
$out = Join-Path $PSScriptRoot 'out'
$callback = 'https://oauch.io/Callback'

function Invoke-Cli([string[]]$arguments, [string]$stdin) {
    if ($stdin) {
        $result = $stdin | docker exec -i $name dotnet /app/valkyrieid.dll @arguments
    }
    else {
        $result = docker exec $name dotnet /app/valkyrieid.dll @arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "valkyrieid $($arguments -join ' ') failed." }
    $result
}

function New-RandomString([int]$bytes) {
    # Works on Windows PowerShell 5.1 and PowerShell 7.
    $buffer = New-Object byte[] $bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($buffer)
    $rng.Dispose()
    [Convert]::ToBase64String($buffer)
}

function Start-Server([string]$masterKey) {
    docker run -d --name $name -p "${Port}:${Port}" -v "${volume}:/data" `
        -e "ASPNETCORE_HTTP_PORTS=$Port" `
        -e "Issuer__Url=$Issuer" `
        -e "MasterKey__Current=$masterKey" `
        -e "ReverseProxy__KnownProxies__0=$KnownProxy" `
        $Image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Starting the container failed.' }

    $discovery = "$($Issuer.TrimEnd('/'))/.well-known/openid-configuration"
    $metadata = $null
    for ($i = 0; $i -lt 60 -and -not $metadata; $i++) {
        Start-Sleep -Seconds 1
        try { $metadata = Invoke-RestMethod -Uri $discovery -TimeoutSec 5 } catch { }
    }
    if (-not $metadata) { throw "$discovery didn't answer. Check the proxy and 'docker logs $name'." }
    if ($metadata.issuer -ne $Issuer.TrimEnd('/')) { throw "Discovery reports issuer $($metadata.issuer), expected $Issuer." }
}

function Get-ContainerEnv([string]$key) {
    $lines = docker inspect $name --format '{{range .Config.Env}}{{println .}}{{end}}'
    if ($LASTEXITCODE -ne 0) { throw "Container $name isn't there; use 'start'." }
    ($lines | Where-Object { $_ -like "$key=*" }) -replace "^$key=", ''
}

switch ($Command) {
    'discover' {
        # A one-off listener on the same port that prints the peer address of each request, as a container sees it.
        Write-Host "Listening on port $Port. Open $Issuer/ in a browser, then press Ctrl+C."
        # No double quotes: Windows PowerShell 5.1 strips them from native command arguments.
        $script = 'import http.server as h' + "`n" +
            'class H(h.BaseHTTPRequestHandler):' + "`n" +
            '    def do_GET(s):' + "`n" +
            '        print(''peer:'', s.client_address[0], ''X-Forwarded-For:'', s.headers.get(''X-Forwarded-For''), ''X-Forwarded-Proto:'', s.headers.get(''X-Forwarded-Proto''), flush=True)' + "`n" +
            '        s.send_response(204); s.end_headers()' + "`n" +
            'h.HTTPServer(('''', ' + $Port + '), H).serve_forever()'
        docker run --rm -p "${Port}:${Port}" python:3-alpine python -c $script
    }
    'start' {
        if (-not $KnownProxy) { throw 'Pass -KnownProxy <ip> (find it with: run.ps1 discover).' }
        docker volume create $volume | Out-Null
        Start-Server (New-RandomString 32)

        # client_credentials needs an API scope.
        Invoke-Cli @('resource', 'create', 'oauch-api', '--scope', 'oauch.read') | Out-Null

        $password = New-RandomString 18
        Invoke-Cli @('user', 'create', 'oauch', '--email', 'oauch@example.test', '--name', 'OAuch Tester') $password | Out-Null

        $scopes = @('--scope', 'openid', '--scope', 'profile', '--scope', 'email', '--scope', 'offline_access')
        $secrets = [ordered]@{}
        foreach ($clientId in @('oauch-1', 'oauch-2')) {
            $output = Invoke-Cli (@('client', 'create', $clientId, '--grant', 'authorization_code', '--grant', 'refresh_token',
                    '--grant', 'client_credentials', '--redirect-uri', $callback, '--scope', 'oauch.read') + $scopes)
            $secrets[$clientId] = ($output | Where-Object { $_ -like 'client_secret: *' }) -replace '^client_secret: ', ''
        }
        Invoke-Cli (@('client', 'create', 'oauch-public', '--public', '--grant', 'authorization_code', '--grant', 'refresh_token',
                '--redirect-uri', $callback) + $scopes) | Out-Null

        New-Item -ItemType Directory -Force -Path $out | Out-Null
        $summary = @"
Issuer:                 $Issuer
Test user:              oauch
Password:               $password
Client ID:              oauch-1
Client secret:          $($secrets['oauch-1'])
Alternative client ID:  oauch-2
Alternative secret:     $($secrets['oauch-2'])
Public client ID:       oauch-public (no secret)
Scope:                  openid profile email offline_access
Scope (client_credentials override): oauch.read
"@
        $summary | Set-Content (Join-Path $out 'oauch.txt')
        Write-Host $summary
        Write-Host "Saved to $out\oauch.txt (contains secrets; removed by 'run.ps1 stop')."
    }
    'restart' {
        # Recreates the container from the (rebuilt) image with the same database, master key, issuer, and proxy, so
        # the clients, secrets, and the OAuch site settings stay valid.
        $masterKey = Get-ContainerEnv 'MasterKey__Current'
        $Issuer = Get-ContainerEnv 'Issuer__Url'
        $KnownProxy = Get-ContainerEnv 'ReverseProxy__KnownProxies__0'
        docker rm -f $name | Out-Null
        Start-Server $masterKey
        Write-Host "Restarted $name from $Image; clients and secrets are unchanged."
    }
    'stop' {
        docker rm -f $name 2>$null | Out-Null
        docker volume rm $volume 2>$null | Out-Null
        if (Test-Path $out) { Remove-Item -Recurse -Force $out }
        Write-Host 'Removed the container, its database, and the saved secrets.'
    }
}
