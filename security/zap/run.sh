#!/usr/bin/env bash
# Runs the OWASP ZAP scans against a throwaway ValkyrieID in Docker: a baseline scan, an API scan of the Admin API
# (unauthenticated), a full active scan of the sign-in pages, and an authenticated active scan of the Admin API.
# Then checks the alerts against rules.tsv (check_alerts.py). Reports are written to security/zap/out/.
# See docs/security/zap.md.
#
# Usage: security/zap/run.sh [--image <image>] [--scan baseline|api|login|admin]... [--keep]
#   --image   scan an existing image instead of building one from this checkout
#   --scan    run only the given scans (repeatable); default: all four
#   --keep    leave ValkyrieID running afterwards
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$DIR/../.." && pwd)"
COMPOSE=(docker compose -f "$DIR/docker-compose.yml")
# `docker compose`, or the standalone docker-compose (e.g. with Podman).
docker compose version > /dev/null 2>&1 || COMPOSE=(docker-compose -f "$DIR/docker-compose.yml")
TARGET=https://valkyrieid.localtest.me:9443
IMAGE=""
KEEP=false
SCANS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --image) IMAGE="$2"; shift ;;
    --scan) SCANS+=("$2"); shift ;;
    --keep) KEEP=true ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
[ ${#SCANS[@]} -gt 0 ] || SCANS=(baseline api login admin)

if [ -z "$IMAGE" ]; then
  IMAGE=valkyrieid:zap
  docker build -t "$IMAGE" "$ROOT"
fi

export VALKYRIEID_IMAGE="$IMAGE"
VALKYRIEID_MASTER_KEY="$(openssl rand -base64 32)"
ZAP_ADMIN_PASSWORD="$(openssl rand -base64 24)"
export VALKYRIEID_MASTER_KEY ZAP_ADMIN_PASSWORD

cleanup() {
  mkdir -p "$DIR/out"
  "${COMPOSE[@]}" logs --no-color valkyrieid > "$DIR/out/valkyrieid.log" 2>&1 || true
  $KEEP || "${COMPOSE[@]}" --profile zap down -v --remove-orphans > /dev/null 2>&1 || true
  # Secrets of the throwaway admin.
  rm -f "$DIR"/out/admin-token-state.json*
}
trap cleanup EXIT

"${COMPOSE[@]}" --profile zap down -v --remove-orphans > /dev/null 2>&1 || true
rm -rf "$DIR/out"
mkdir -p "$DIR/out"
chmod a+rwx "$DIR/out"
cp "$ROOT/docs/public/openapi/admin-api.json" "$DIR/out/admin-api.json"
"${COMPOSE[@]}" up -d --wait valkyrieid

CLI=("${COMPOSE[@]}" exec -T valkyrieid dotnet /app/valkyrieid.dll)
printf '%s\n' "$ZAP_ADMIN_PASSWORD" | "${CLI[@]}" user create zapadmin --email zapadmin@example.test > /dev/null
"${CLI[@]}" admin grant-admin zapadmin > /dev/null

ZAP=("${COMPOSE[@]}" run --rm zap)
REPORTS=()
for scan in "${SCANS[@]}"; do
  case "$scan" in
    # -I: warnings don't fail the scan; check_alerts.py decides.
    baseline) "${ZAP[@]}" zap-baseline.py -t "$TARGET/" -I -J out/baseline.json -r out/baseline.html ;;
    api) "${ZAP[@]}" zap-api-scan.py -t /zap/wrk/out/admin-api.json -f openapi -O "$TARGET" -I -J out/api.json -r out/api.html ;;
    login) "${ZAP[@]}" zap.sh -cmd -autorun /zap/wrk/login-pages.yaml \
      -config 'anticsrf.tokens.token(0).name=__RequestVerificationToken' -config 'anticsrf.tokens.token(0).enabled=true' ;;
    admin) "${ZAP[@]}" zap.sh -cmd -autorun /zap/wrk/admin-api.yaml ;;
    *) echo "Unknown scan: $scan" >&2; exit 2 ;;
  esac
  case "$scan" in
    login) REPORTS+=("$DIR/out/login-pages.json") ;;
    admin) REPORTS+=("$DIR/out/admin-api-auth.json") ;;
    *) REPORTS+=("$DIR/out/$scan.json") ;;
  esac
done

python3 "$DIR/check_alerts.py" "${REPORTS[@]}"
