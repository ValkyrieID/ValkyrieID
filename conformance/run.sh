#!/usr/bin/env bash
# Runs the OpenID conformance plans against ValkyrieID in Docker: builds the image, starts the suite and ValkyrieID
# (docker-compose.yml), registers the test user and clients (setup.sh --docker), runs the plans (run-plans.py), and
# removes everything again. Results and logs are written to conformance/out/results/. Used by CI and locally.
# See docs/operations/conformance.md.
#
# Usage: conformance/run.sh [--full] [--image <image>] [--keep]
#   --full    also run the client_secret_post and form_post variants (release tags)
#   --image   test an existing image instead of building one from this checkout
#   --keep    leave the containers running afterwards (the suite stays at https://localhost.emobix.co.uk:8443)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIR="$ROOT/conformance"
COMPOSE=(docker compose -f "$DIR/docker-compose.yml")
# `docker compose`, or the standalone docker-compose (e.g. with Podman).
docker compose version > /dev/null 2>&1 || COMPOSE=(docker-compose -f "$DIR/docker-compose.yml")
PLANS=default
IMAGE=""
KEEP=false
while [ $# -gt 0 ]; do
  case "$1" in
    --full) PLANS=full ;;
    --image) IMAGE="$2"; shift ;;
    --keep) KEEP=true ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

if [ -z "$IMAGE" ]; then
  IMAGE=valkyrieid:conformance
  docker build -t "$IMAGE" "$ROOT"
fi

export VALKYRIEID_IMAGE="$IMAGE" PLANS
VALKYRIEID_MASTER_KEY="$(openssl rand -base64 32)"
export VALKYRIEID_MASTER_KEY

cleanup() {
  mkdir -p "$DIR/out/results"
  "${COMPOSE[@]}" logs --no-color valkyrieid > "$DIR/out/results/valkyrieid.log" 2>&1 || true
  "${COMPOSE[@]}" logs --no-color server > "$DIR/out/results/suite.log" 2>&1 || true
  if ! $KEEP; then
    "${COMPOSE[@]}" --profile runner down -v --remove-orphans > /dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

"${COMPOSE[@]}" --profile runner down -v --remove-orphans > /dev/null 2>&1 || true
rm -rf "$DIR/out"
"${COMPOSE[@]}" up -d --wait valkyrieid nginx
bash "$DIR/setup.sh" --docker
"${COMPOSE[@]}" run --rm runner
