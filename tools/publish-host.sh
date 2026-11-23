#!/usr/bin/env bash
# Run from the repository root after building OAR and the native terminal bridge.
set -euo pipefail
output=${1:?usage: bash tools/publish-host.sh OUTPUT_DIR BRIDGE_BINARY}
bridge=${2:?missing terminal bridge binary}
test -s tools/perga-oar-bridge/dist/perga-oar-bridge.mjs
test -x "$bridge"
mkdir "$output"
dotnet publish src/Perga.Host/Perga.Host.csproj --configuration Release --output "$output"
test -s "$output/oar-bridge/perga-oar-bridge.mjs"
install -m 755 "$bridge" "$output/perga-term-bridge"
cp -R templates LICENSE "$output/"
