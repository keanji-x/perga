#!/usr/bin/env bash
# Exercise an extracted self-contained release, including the assets requested by a browser.
set -euo pipefail
cd "${1:?usage: bash tools/smoke-package.sh EXTRACTED_PACKAGE_DIR}"
test -x Perga.Host
test -x perga-term-bridge
test -s templates/agents-delivery/template.perga.js
echo '{"jsonrpc":"2.0","id":1,"method":"session/close"}' |
  node oar-bridge/perga-oar-bridge.mjs | jq -es '.[0] | .id == 1 and .result.closed == true'

tmp=$(mktemp -d)
export PERGA_DATA_DIR="$tmp/data"
export Perga__EagerStartTerminalBridge=true
# Unset DOTNET_ROOT and PERGA_STATIC_WEB_DIR to verify the self-contained Host
# launches without an external .NET runtime and auto-discovers its bundled web/ directory.
env -u DOTNET_ROOT -u DOTNET_MULTILEVEL_LOOKUP -u ASPNETCORE_URLS -u Kestrel__Endpoints__Http__Url -u PERGA_STATIC_WEB_DIR \
  ./Perga.Host --urls http://127.0.0.1:0 > "$tmp/host.log" 2>&1 &
host_pid=$!
cleanup() {
  result=$?
  kill "$host_pid" 2>/dev/null || true
  wait "$host_pid" || true
  if (( result != 0 )); then cat "$tmp/host.log" >&2; fi
  rm -rf "$tmp"
}
trap cleanup EXIT

# Eager startup waits for the bridge to bind before Kestrel accepts requests.
for ((attempt=0; attempt<30; attempt++)); do
  url=$(sed -n 's/.*Now listening on: \(http:\/\/127\.0\.0\.1:[0-9]*\).*/\1/p' "$tmp/host.log")
  if [[ -n "$url" ]]; then break; fi
  kill -0 "$host_pid"
  sleep 1
done
test -n "$url"
curl -fsS "$url/healthz" | jq -es '.[0] | .status == "ok" and .service == "perga"'
curl -fsS "$url/" -o "$tmp/index.html"
cmp web/index.html "$tmp/index.html"
asset=$(sed -n 's/.*<script[^>]* src="\(\/[^"]*\.js\)".*/\1/p' "$tmp/index.html")
test -n "$asset"
curl -fsS "$url$asset" -o "$tmp/entry.js"
cmp "web$asset" "$tmp/entry.js"
curl -fsS "$url/track/smoke" -o "$tmp/route.html"
cmp web/index.html "$tmp/route.html"
