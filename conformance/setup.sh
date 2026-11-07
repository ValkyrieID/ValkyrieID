#!/usr/bin/env bash
# Registers the test user and clients for the OpenID Foundation conformance suite (Basic OP, Config OP,
# RP-Initiated Logout, and Back-Channel Logout) and writes the plan configurations to conformance/out/.
# See docs/operations/conformance.md.
#
# Usage: conformance/setup.sh [suite-base-url] [alias]
#        conformance/setup.sh --docker
# Without --docker, requires the same configuration as the server (Database__*, MasterKey__Current, Issuer__Url).
# With --docker, runs the CLI in the valkyrieid service of conformance/docker-compose.yml, which must be running,
# and uses the suite in the same Compose project (run.sh does this).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/conformance/out"
DOCKER=false
if [ "${1:-}" = "--docker" ]; then
  DOCKER=true
  shift
fi

if $DOCKER; then
  SUITE="${1:-https://localhost.emobix.co.uk:8443}"
  ALIAS="${2:-valkyrieid}"
  ISSUER="https://valkyrieid.localtest.me:9443"
  CLI=(docker compose -f "$ROOT/conformance/docker-compose.yml" exec -T valkyrieid dotnet /app/valkyrieid.dll)
  # `docker compose`, or the standalone docker-compose (e.g. with Podman).
  docker compose version > /dev/null 2>&1 || CLI=(docker-compose "${CLI[@]:2}")
else
  SUITE="${1:-https://www.certification.openid.net}"
  ALIAS="${2:-valkyrieid}"
  : "${Issuer__Url:?Set Issuer__Url to the public issuer URL of the server under test}"
  ISSUER="${Issuer__Url%/}"
  CLI=(dotnet run --project "$ROOT/src/ValkyrieID.Server" --no-launch-profile --)
fi

CALLBACK="${SUITE%/}/test/a/$ALIAS/callback"
POST_LOGOUT_REDIRECT="${SUITE%/}/test/a/$ALIAS/post_logout_redirect"
BACKCHANNEL_LOGOUT="${SUITE%/}/test/a/$ALIAS/backchannel_logout"

mkdir -p "$OUT"
PASSWORD="$(openssl rand -base64 24)"

printf '%s\n' "$PASSWORD" | "${CLI[@]}" user create conformance --email conformance@example.test --name "Conformance Tester"

create_client() {
  local client_id="$1"
  shift
  "${CLI[@]}" client create "$client_id" --grant authorization_code --grant refresh_token --redirect-uri "$CALLBACK" \
    --scope openid --scope profile --scope email --scope roles --scope groups --scope offline_access --no-pkce --allow-secret-post "$@" \
    | sed -n 's/^client_secret: //p' | tr -d '\r'
}

SECRET1="$(create_client conformance-1)"
SECRET2="$(create_client conformance-2)"
# The logout plans use their own clients: only the Back-Channel Logout plan's clients have a back-channel logout URI,
# because the other plans fail on unexpected logout tokens.
RP_LOGOUT_SECRET1="$(create_client conformance-rp-logout-1 --post-logout-redirect-uri "$POST_LOGOUT_REDIRECT")"
RP_LOGOUT_SECRET2="$(create_client conformance-rp-logout-2 --post-logout-redirect-uri "$POST_LOGOUT_REDIRECT")"
LOGOUT_OPTIONS=(--post-logout-redirect-uri "$POST_LOGOUT_REDIRECT" --backchannel-logout-uri "$BACKCHANNEL_LOGOUT")
LOGOUT_SECRET1="$(create_client conformance-logout-1 "${LOGOUT_OPTIONS[@]}")"
LOGOUT_SECRET2="$(create_client conformance-logout-2 "${LOGOUT_OPTIONS[@]}")"

cat > "$OUT/basic-op.json" <<EOF
{
  "alias": "$ALIAS",
  "description": "ValkyrieID Basic OP",
  "server": { "discoveryUrl": "$ISSUER/.well-known/openid-configuration" },
  "client": { "client_id": "conformance-1", "client_secret": "$SECRET1" },
  "client2": { "client_id": "conformance-2", "client_secret": "$SECRET2" },
  "client_secret_post": { "client_id": "conformance-1", "client_secret": "$SECRET1" }
}
EOF

cat > "$OUT/config-op.json" <<EOF
{
  "alias": "$ALIAS-config",
  "description": "ValkyrieID Config OP",
  "server": { "discoveryUrl": "$ISSUER/.well-known/openid-configuration" }
}
EOF

write_logout_plan() {
  cat > "$OUT/$1.json" <<EOF
{
  "alias": "$ALIAS",
  "description": "$2",
  "server": { "discoveryUrl": "$ISSUER/.well-known/openid-configuration" },
  "client": { "client_id": "$3-1", "client_secret": "$4" },
  "client2": { "client_id": "$3-2", "client_secret": "$5" }
}
EOF
}
write_logout_plan rp-initiated-logout "ValkyrieID RP-Initiated Logout" conformance-rp-logout "$RP_LOGOUT_SECRET1" "$RP_LOGOUT_SECRET2"
write_logout_plan backchannel-logout "ValkyrieID Back-Channel Logout" conformance-logout "$LOGOUT_SECRET1" "$LOGOUT_SECRET2"

if $DOCKER; then
  # Read by run-plans.py to fill in the browser automation (browser.json).
  cat > "$OUT/test-user.json" <<EOF
{ "username": "conformance", "password": "$PASSWORD" }
EOF
fi

echo "Wrote basic-op.json, config-op.json, rp-initiated-logout.json, and backchannel-logout.json to $OUT."
$DOCKER || echo "Test user: conformance  password: $PASSWORD"
echo "The files contain client secrets; delete conformance/out/ when you're done."
