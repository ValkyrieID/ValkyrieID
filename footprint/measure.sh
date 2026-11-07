#!/usr/bin/env sh
# Measures the container footprint against the targets in PROJECT.md §11: image size, startup to ready (SQLite,
# fresh volume), and idle memory after a settle period. Exits with 1 when a target is exceeded.
# See docs/guide/docker.md#footprint.
#
# Usage: footprint/measure.sh [--image <name>] [--skip-build] [--settle <seconds>] [--port <port>]
#                             [--max-image-mb 210] [--max-memory-mb 150] [--max-startup-seconds 4]
# Requires Docker (or Podman with the docker CLI), curl, and awk.
set -eu

image=valkyrieid:footprint
skip_build=false
settle=30
port=18080
max_image_mb=210
max_memory_mb=150
max_startup_seconds=4

while [ $# -gt 0 ]; do
  case "$1" in
    --image) image=$2; shift 2 ;;
    --skip-build) skip_build=true; shift ;;
    --settle) settle=$2; shift 2 ;;
    --port) port=$2; shift 2 ;;
    --max-image-mb) max_image_mb=$2; shift 2 ;;
    --max-memory-mb) max_memory_mb=$2; shift 2 ;;
    --max-startup-seconds) max_startup_seconds=$2; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

root=$(cd "$(dirname "$0")/.." && pwd)
container="valkyrieid-footprint-$$"
volume="valkyrieid-footprint-$$"

if [ "$skip_build" = false ]; then
  docker build -t "$image" "$root"
fi

now() { date +%s.%N; }
master_key=$(head -c 32 /dev/urandom | base64 | tr -d '\n')
image_mb=$(docker image inspect "$image" --format '{{.Size}}' | awk '{ printf "%.1f", $1 / 1048576 }')

cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker volume create "$volume" >/dev/null
# The periodic health check is off while measuring: each probe starts a second dotnet process in the container,
# whose memory docker stats would count. The command is run once explicitly below.
docker create --name "$container" --no-healthcheck -p "127.0.0.1:$port:8080" -v "$volume:/data" \
  -e Issuer__Url=https://id.example.test -e "MasterKey__Current=$master_key" "$image" >/dev/null

start=$(now)
docker start "$container" >/dev/null
ready=false
while awk -v s="$start" -v n="$(now)" 'BEGIN { exit !(n - s < 60) }'; do
  if curl -fs -o /dev/null --max-time 2 "http://127.0.0.1:$port/health/ready"; then
    ready=true
    break
  fi
  sleep 0.02
done
startup=$(awk -v s="$start" -v n="$(now)" 'BEGIN { printf "%.2f", n - s }')
if [ "$ready" = false ]; then
  docker logs "$container"
  echo "The server did not become ready within 60 seconds." >&2
  exit 1
fi

# The image's own health check command, as the container runtime runs it (no shell in the image).
docker exec "$container" dotnet /app/valkyrieid.dll healthcheck

sleep "$settle"
memory_mb=$(docker stats --no-stream --format '{{.MemUsage}}' "$container" | awk '{
  split($0, parts, "/"); value = parts[1]; gsub(/ /, "", value)
  match(value, /^[0-9.]+/); number = substr(value, 1, RLENGTH); unit = substr(value, RLENGTH + 1)
  if (unit == "B") number /= 1048576
  else if (unit == "kB" || unit == "KB" || unit == "KiB") number /= 1024
  else if (unit == "GB" || unit == "GiB") number *= 1024
  else if (unit != "MB" && unit != "MiB") { print "unexpected unit " unit > "/dev/stderr"; exit 1 }
  printf "%.1f", number
}')

failed=false
report() {
  if awk -v v="$2" -v t="$3" 'BEGIN { exit !(v < t) }'; then result=pass; else result=FAIL; failed=true; fi
  printf '%-22s %8s %8s  %s\n' "$1" "$2" "$3" "$result"
}

printf '%-22s %8s %8s  %s\n' Metric Value Target Result
report 'Image size (MB)' "$image_mb" "$max_image_mb"
report 'Startup to ready (s)' "$startup" "$max_startup_seconds"
report 'Idle memory (MB)' "$memory_mb" "$max_memory_mb"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo '| Metric | Value | Target |'
    echo '|---|---|---|'
    echo "| Image size (MB) | $image_mb | < $max_image_mb |"
    echo "| Startup to ready (s) | $startup | < $max_startup_seconds |"
    echo "| Idle memory (MB) | $memory_mb | < $max_memory_mb |"
  } >>"$GITHUB_STEP_SUMMARY"
fi

if [ "$failed" = true ]; then
  echo 'Footprint targets exceeded.'
  exit 1
fi
