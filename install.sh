#!/usr/bin/env bash
# Install or upgrade the self-contained Perga release for macOS (Apple Silicon) or Linux x64.
set -euo pipefail

REPO="${PERGA_REPO:-keanji-x/perga}"
VERSION="${PERGA_VERSION:-latest}"
INSTALL_DIR="${PERGA_INSTALL_DIR:-$HOME/.local/share/perga-app}"
BIN_DIR="${PERGA_BIN_DIR:-$HOME/.local/bin}"
DATA_DIR="${PERGA_DATA_DIR:-$HOME/.local/share/perga}"

need_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "error: required command '$1' was not found on PATH." >&2
    exit 1
  fi
}

need_cmd curl
need_cmd tar

case "$(uname -sm)" in
  "Linux x86_64")  PACKAGE="perga-linux-x64" ;;
  "Darwin arm64")  PACKAGE="perga-darwin-arm64" ;;
  *)
    echo "error: unsupported platform '$(uname -sm)'." >&2
    echo "Perga currently ships self-contained binaries for Linux x86_64 and macOS Apple Silicon (arm64)." >&2
    exit 1
    ;;
esac

if [[ -n "${PERGA_BASE_URL:-}" ]]; then
  BASE_URL="${PERGA_BASE_URL%/}"
elif [[ "$VERSION" == "latest" ]]; then
  BASE_URL="https://github.com/${REPO}/releases/latest/download"
else
  BASE_URL="https://github.com/${REPO}/releases/download/${VERSION}"
fi

ARCHIVE="${PACKAGE}.tar.gz"
CHECKSUM="${ARCHIVE}.sha256"

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

echo "Downloading ${ARCHIVE} (${VERSION})..."
curl --fail --silent --show-error --location --retry 3 --max-time 300 \
  --output "${tmp}/${ARCHIVE}" "${BASE_URL}/${ARCHIVE}"
curl --fail --silent --show-error --location --retry 3 --max-time 60 \
  --output "${tmp}/${CHECKSUM}" "${BASE_URL}/${CHECKSUM}"

echo "Verifying SHA-256 checksum..."
(
  cd "$tmp"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum -c "$CHECKSUM"
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 -c "$CHECKSUM"
  else
    echo "error: neither sha256sum nor shasum is installed." >&2
    exit 1
  fi
)

echo "Extracting into ${INSTALL_DIR}..."
mkdir -p "${tmp}/extract"
tar -xzf "${tmp}/${ARCHIVE}" -C "${tmp}/extract"
if [[ ! -x "${tmp}/extract/perga/Perga.Host" ]]; then
  echo "error: archive did not contain an executable perga/Perga.Host binary." >&2
  exit 1
fi

parent_dir=$(dirname "$INSTALL_DIR")
mkdir -p "$parent_dir" "$BIN_DIR" "$DATA_DIR"
stage_dir="${parent_dir}/.perga-stage-$$"
backup_dir="${parent_dir}/.perga-prev-$$"
rm -rf "$stage_dir" "$backup_dir"
mv "${tmp}/extract/perga" "$stage_dir"
if [[ -e "$INSTALL_DIR" ]]; then
  mv "$INSTALL_DIR" "$backup_dir"
fi
if ! mv "$stage_dir" "$INSTALL_DIR"; then
  if [[ -e "$backup_dir" ]]; then
    mv "$backup_dir" "$INSTALL_DIR" || true
  fi
  exit 1
fi
rm -rf "$backup_dir"

cat > "${BIN_DIR}/perga" <<EOF
#!/usr/bin/env bash
set -euo pipefail
INSTALL_DIR="\${PERGA_INSTALL_DIR:-${INSTALL_DIR}}"
export PERGA_DATA_DIR="\${PERGA_DATA_DIR:-${DATA_DIR}}"
export PERGA_STATIC_WEB_DIR="\${PERGA_STATIC_WEB_DIR:-\$INSTALL_DIR/web}"

case "\${1:-}" in
  -h|--help|help)
    cat <<'USAGE'
Usage:
  perga [host args...]               Start Perga on http://127.0.0.1:4041 (override with PERGA_URLS)
  perga start [host args...]         Same as 'perga'
  perga auth <subcommand> [args...]  Manage owner auth (e.g. perga auth init-owner --username owner --allow-migrate)
USAGE
    exit 0
    ;;
  auth)
    exec "\$INSTALL_DIR/Perga.Host" "\$@"
    ;;
  start)
    shift
    exec "\$INSTALL_DIR/Perga.Host" --urls "\${PERGA_URLS:-http://127.0.0.1:4041}" "\$@"
    ;;
  *)
    exec "\$INSTALL_DIR/Perga.Host" --urls "\${PERGA_URLS:-http://127.0.0.1:4041}" "\$@"
    ;;
esac
EOF
chmod 755 "${BIN_DIR}/perga"

echo ""
echo "Installed Perga to ${INSTALL_DIR}"
echo "Launcher:          ${BIN_DIR}/perga"
echo "Data directory:    ${DATA_DIR}"

if command -v node >/dev/null 2>&1; then
  node_major=$(node -p 'Number(process.versions.node.split(".")[0])' 2>/dev/null || echo 0)
  if (( node_major < 24 )); then
    echo ""
    echo "Note: Node.js 24+ is required for the bundled OAR agent bridge (found $(node -v))."
  fi
else
  echo ""
  echo "Note: Node.js 24+ is required for the bundled OAR agent bridge. Install Node.js 24+ and your CLI (claude / codex)."
fi

case ":${PATH}:" in
  *":${BIN_DIR}:"*) cmd="perga" ;;
  *)
    echo ""
    echo "Add ${BIN_DIR} to your PATH:"
    echo "  export PATH=\"${BIN_DIR}:\$PATH\""
    cmd="${BIN_DIR}/perga"
    ;;
esac

echo ""
if [[ ! -f "${DATA_DIR}/perga.db" ]]; then
  echo "Next steps (first run):"
  echo "  1. Create your owner account:"
  echo "     ${cmd} auth init-owner --username owner --allow-migrate"
  echo "  2. Start Perga:"
  echo "     ${cmd}"
  echo "  3. Open http://127.0.0.1:4041/ in your browser."
else
  echo "Existing data preserved at ${DATA_DIR}. Start Perga with:"
  echo "  ${cmd}"
fi
