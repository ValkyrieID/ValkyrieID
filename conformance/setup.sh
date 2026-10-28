#!/usr/bin/env bash
# Registers the test user and clients for the OpenID Foundation conformance suite (Basic OP and Config OP)
# and writes the plan configurations to conformance/out/. See docs/operations/conformance.md.
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
CLI=(dotnet run --project "$ROOT/src/ValkyrieID.Server" --no-launch-profile --)

mkdir -p "$OUT"
PASSWORD="$(openssl rand -base64 24)"

printf '%s\n' "$PASSWORD" | "${CLI[@]}" user create conformance --email conformance@example.test --name "Conformance Tester"

create_client() {
  "${CLI[@]}" client create "$1" --grant authorization_code --grant refresh_token --redirect-uri "$CALLBACK" \
    --scope openid --scope profile --scope email --scope roles --scope groups --scope offline_access --no-pkce \
    | sed -n 's/^client_secret: //p'
}

SECRET1="$(create_client conformance-1)"
SECRET2="$(create_client conformance-2)"

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

echo "Wrote $OUT/basic-op.json and $OUT/config-op.json."
echo "Test user: conformance  password: $PASSWORD"
echo "The files contain client secrets; delete conformance/out/ when you're done."
