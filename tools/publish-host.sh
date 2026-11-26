#!/usr/bin/env bash
# Run from the repository root after building OAR and the native terminal bridge.
set -euo pipefail
output=${1:?usage: bash tools/publish-host.sh OUTPUT_DIR BRIDGE_BINARY [RID]}
bridge=${2:?missing terminal bridge binary}
rid=${3:-}
if [[ -z "$rid" ]]; then
  case "$(uname -sm)" in
    "Linux x86_64") rid=linux-x64 ;;
    "Darwin arm64") rid=osx-arm64 ;;
    *) echo "Unsupported platform: $(uname -sm). Pass RID explicitly (linux-x64 or osx-arm64)." >&2; exit 1 ;;
  esac
fi

test -s tools/perga-oar-bridge/dist/perga-oar-bridge.mjs
test -x "$bridge"
mkdir -p "$output"
dotnet publish src/Perga.Host/Perga.Host.csproj \
  --configuration Release \
  --runtime "$rid" \
  --self-contained true \
  --output "$output"
test -x "$output/Perga.Host"
test -s "$output/oar-bridge/perga-oar-bridge.mjs"
install -m 755 "$bridge" "$output/perga-term-bridge"
cp -R templates LICENSE "$output/"
version="${PERGA_RELEASE_VERSION:-$(git describe --tags --always --dirty 2>/dev/null || echo dev)}"
printf '%s\n' "$version" > "$output/VERSION"
