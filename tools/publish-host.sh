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
# `perga version` and the installer report the first line of VERSION.
version="${PERGA_RELEASE_VERSION:-}"
if [[ -z "$version" ]]; then
  version=$(git describe --tags --exact-match 2>/dev/null || printf 'dev-%s' "$(git rev-parse --short=8 HEAD 2>/dev/null || echo unknown)")
fi
printf '%s\n' "$version" > "$output/VERSION"
