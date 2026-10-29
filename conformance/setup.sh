#!/usr/bin/env bash
# Registers the test user and clients for the OpenID Foundation conformance suite (Basic OP, Config OP,
# RP-Initiated Logout, and Back-Channel Logout) and writes the plan configurations to conformance/out/.
# See docs/operations/conformance.md.
#
# Usage: conformance/setup.sh [suite-base-url] [alias]
# Requires the same configuration as the server (Database__*, MasterKey__Current, Issuer__Url).
set -euo pipefail

SUITE="${1:-https://www.certification.openid.net}"
ALIAS="${2:-valkyrieid}"
: "${Issuer__Url:?Set Issuer__Url to the public issuer URL of the server under test}"
ISSUER="${Issuer__Url%/}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/conformance/out"
CALLBACK="${SUITE%/}/test/a/$ALIAS/callback"
POST_LOGOUT_REDIRECT="${SUITE%/}/test/a/$ALIAS/post_logout_redirect"
BACKCHANNEL_LOGOUT="${SUITE%/}/test/a/$ALIAS/backchannel_logout"
CLI=(dotnet run --project "$ROOT/src/ValkyrieID.Server" --no-launch-profile --)

mkdir -p "$OUT"
PASSWORD="$(openssl rand -base64 24)"

printf '%s\n' "$PASSWORD" | "${CLI[@]}" user create conformance --email conformance@example.test --name "Conformance Tester"

create_client() {
  local client_id="$1"
  shift
  "${CLI[@]}" client create "$client_id" --grant authorization_code --grant refresh_token --redirect-uri "$CALLBACK" \
    --scope openid --scope profile --scope email --scope roles --scope groups --scope offline_access --no-pkce --allow-secret-post "$@" \
    | sed -n 's/^client_secret: //p'
}

SECRET1="$(create_client conformance-1)"
SECRET2="$(create_client conformance-2)"
# The logout plans use their own clients, so the Basic OP clients don't receive back-channel logout requests.
LOGOUT_OPTIONS=(--post-logout-redirect-uri "$POST_LOGOUT_REDIRECT" --backchannel-logout-uri "$BACKCHANNEL_LOGOUT")
LOGOUT_SECRET1="$(create_client conformance-logout-1 "${LOGOUT_OPTIONS[@]}")"
LOGOUT_SECRET2="$(create_client conformance-logout-2 "${LOGOUT_OPTIONS[@]}")"

cat > "$OUT/basic-op.json" <<EOF
{
  "alias": "$ALIAS",
  "description": "ValkyrieID Basic OP",
  "server": { "discoveryUrl": "$ISSUER/.well-known/openid-configuration" },
  "client": { "client_id": "conformance-1", "client_secret": "$SECRET1" },
  "client2": { "client_id": "conformance-2", "client_secret": "$SECRET2" }
}
EOF

cat > "$OUT/config-op.json" <<EOF
{
  "alias": "$ALIAS-config",
  "description": "ValkyrieID Config OP",
  "server": { "discoveryUrl": "$ISSUER/.well-known/openid-configuration" }
}
EOF

for plan in "rp-initiated-logout:ValkyrieID RP-Initiated Logout" "backchannel-logout:ValkyrieID Back-Channel Logout"; do
  cat > "$OUT/${plan%%:*}.json" <<EOF
{
  "alias": "$ALIAS",
  "description": "${plan#*:}",
  "server": { "discoveryUrl": "$ISSUER/.well-known/openid-configuration" },
  "client": { "client_id": "conformance-logout-1", "client_secret": "$LOGOUT_SECRET1" },
  "client2": { "client_id": "conformance-logout-2", "client_secret": "$LOGOUT_SECRET2" }
}
EOF
done

echo "Wrote basic-op.json, config-op.json, rp-initiated-logout.json, and backchannel-logout.json to $OUT."
echo "Test user: conformance  password: $PASSWORD"
echo "The files contain client secrets; delete conformance/out/ when you're done."
