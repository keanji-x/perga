#!/usr/bin/env bash
# Install or upgrade the self-contained Perga release for macOS (Apple Silicon) or Linux x64.
set -euo pipefail

REPO="${PERGA_REPO:-keanji-x/perga}"
VERSION="${PERGA_VERSION:-latest}"
RAW_INSTALL_DIR="${PERGA_INSTALL_DIR:-$HOME/.local/share/perga-app}"
RAW_BIN_DIR="${PERGA_BIN_DIR:-$HOME/.local/bin}"
RAW_DATA_DIR="${PERGA_DATA_DIR:-$HOME/.local/share/perga}"

need_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "error: required command '$1' was not found on PATH." >&2
    exit 1
  fi
}

# Resolve a path (existing or not) to an absolute, canonical physical path so
# relative paths, "..", and symlinked ancestors cannot fool containment checks
# or break the persistent launcher across working directories.
resolve_physical_path() {
  local raw="$1"
  local name="$2"
  if [[ -z "$raw" ]]; then
    echo "error: ${name} cannot be empty." >&2
    exit 1
  fi

  local path="$raw"
  if [[ "$path" != /* ]]; then
    path="$(pwd -P)/${path}"
  fi

  local resolved="/"
  local missing_tail=0
  local IFS='/'
  local -a segments=()
  read -r -a segments <<< "$path"

  local seg candidate parent_phys
  for seg in "${segments[@]}"; do
    if [[ -z "$seg" || "$seg" == "." ]]; then
      continue
    fi
    if [[ "$seg" == ".." ]]; then
      if [[ "$resolved" != "/" ]]; then
        resolved="${resolved%/*}"
        [[ -z "$resolved" ]] && resolved="/"
      fi
      if [[ -d "$resolved" ]]; then
        resolved=$(cd -P -- "$resolved" && pwd -P)
        missing_tail=0
      fi
      continue
    fi

    if [[ "$resolved" == "/" ]]; then
      candidate="/${seg}"
    else
      candidate="${resolved}/${seg}"
    fi

    if (( missing_tail == 0 )) && [[ -d "$candidate" ]]; then
      resolved=$(cd -P -- "$candidate" && pwd -P)
    elif (( missing_tail == 0 )) && [[ -L "$candidate" ]]; then
      parent_phys=$(cd -P -- "$resolved" && pwd -P)
      if [[ "$parent_phys" == "/" ]]; then
        resolved="/${seg}"
      else
        resolved="${parent_phys}/${seg}"
      fi
      missing_tail=1
    else
      resolved="$candidate"
      missing_tail=1
    fi
  done

  if [[ "$resolved" == "/" ]]; then
    echo "error: refusing to use '/' for ${name}." >&2
    exit 1
  fi
  printf '%s' "$resolved"
}

quote_sh() {
  local s="$1"
  printf "'%s'" "${s//\'/\'\\\'\'}"
}

need_cmd curl
need_cmd tar

if [[ -L "${RAW_INSTALL_DIR%/}" ]]; then
  echo "error: PERGA_INSTALL_DIR (${RAW_INSTALL_DIR}) must be a real directory, not a symlink." >&2
  exit 1
fi

INSTALL_DIR=$(resolve_physical_path "$RAW_INSTALL_DIR" "PERGA_INSTALL_DIR")
BIN_DIR=$(resolve_physical_path "$RAW_BIN_DIR" "PERGA_BIN_DIR")
DATA_DIR=$(resolve_physical_path "$RAW_DATA_DIR" "PERGA_DATA_DIR")

if [[ "$DATA_DIR" == "$INSTALL_DIR" || "$DATA_DIR" == "${INSTALL_DIR}/"* ]]; then
  echo "error: PERGA_DATA_DIR (${DATA_DIR}) must not be inside PERGA_INSTALL_DIR (${INSTALL_DIR}), or upgrades would overwrite your data." >&2
  exit 1
fi

if [[ -e "$INSTALL_DIR" && ! -d "$INSTALL_DIR" ]]; then
  echo "error: PERGA_INSTALL_DIR (${INSTALL_DIR}) exists and is not a directory." >&2
  exit 1
fi

if [[ -d "$INSTALL_DIR" ]] && [[ -n "$(ls -A "$INSTALL_DIR")" ]]; then
  if [[ ! -f "${INSTALL_DIR}/.perga-install-marker" && ! -x "${INSTALL_DIR}/Perga.Host" ]]; then
    echo "error: PERGA_INSTALL_DIR (${INSTALL_DIR}) is a non-empty directory that does not look like an existing Perga installation." >&2
    echo "Refusing to replace it. Choose a dedicated directory such as \$HOME/.local/share/perga-app." >&2
    exit 1
  fi
fi

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
stage_dir=""
backup_dir=""
cleanup() {
  rm -rf "$tmp"
  if [[ -n "$stage_dir" && -e "$stage_dir" ]]; then
    rm -rf "$stage_dir"
  fi
}
trap cleanup EXIT

download_asset() {
  local url="$1"
  local dest="$2"
  local max_time="$3"
  local http_code
  http_code=$(curl --silent --show-error --location --retry 3 --max-time "$max_time" \
    --output "$dest" --write-out '%{http_code}' "$url") || return 1
  if [[ "$http_code" != "200" ]]; then
    rm -f "$dest"
    if [[ "$http_code" == "404" && -z "${PERGA_BASE_URL:-}" ]]; then
      echo "error: release asset not found (HTTP 404): ${url}" >&2
      echo "No published GitHub Release matching '${VERSION}' was found for ${PACKAGE}." >&2
      echo "Check https://github.com/${REPO}/releases or build from source via README.md." >&2
    else
      echo "error: failed to download ${url} (HTTP ${http_code})." >&2
    fi
    return 1
  fi
}

echo "Downloading ${ARCHIVE} (${VERSION})..."
download_asset "${BASE_URL}/${ARCHIVE}" "${tmp}/${ARCHIVE}" 300
download_asset "${BASE_URL}/${CHECKSUM}" "${tmp}/${CHECKSUM}" 60

echo "Verifying SHA-256 checksum..."
expected_hash=$(awk -v target="$ARCHIVE" '
  NF >= 2 {
    file = $2
    sub(/^\*/, "", file)
    sub(/^\.\//, "", file)
    if (file == target) {
      print tolower($1)
      count++
    }
  }
  END { if (count != 1) exit 1 }
' "${tmp}/${CHECKSUM}") || {
  echo "error: checksum file ${CHECKSUM} did not contain a unique entry for ${ARCHIVE}." >&2
  exit 1
}

if [[ ! "$expected_hash" =~ ^[0-9a-f]{64}$ ]]; then
  echo "error: invalid SHA-256 digest in ${CHECKSUM}." >&2
  exit 1
fi

if command -v sha256sum >/dev/null 2>&1; then
  actual_hash=$(sha256sum "${tmp}/${ARCHIVE}" | awk '{print tolower($1)}')
elif command -v shasum >/dev/null 2>&1; then
  actual_hash=$(shasum -a 256 "${tmp}/${ARCHIVE}" | awk '{print tolower($1)}')
else
  echo "error: neither sha256sum nor shasum is installed." >&2
  exit 1
fi

if [[ "$actual_hash" != "$expected_hash" ]]; then
  echo "error: SHA-256 mismatch for ${ARCHIVE}." >&2
  echo "  expected: ${expected_hash}" >&2
  echo "  actual:   ${actual_hash}" >&2
  exit 1
fi

echo "Extracting into ${INSTALL_DIR}..."
parent_dir=$(dirname "$INSTALL_DIR")
mkdir -p "$parent_dir" "$BIN_DIR" "$DATA_DIR"
stage_dir=$(mktemp -d "${parent_dir}/.perga-stage.XXXXXX")
tar -xzf "${tmp}/${ARCHIVE}" -C "${tmp}"
if [[ ! -x "${tmp}/perga/Perga.Host" ]]; then
  echo "error: archive did not contain an executable perga/Perga.Host binary." >&2
  exit 1
fi
rmdir "$stage_dir"
mv "${tmp}/perga" "$stage_dir"
printf 'perga-install-v1\n' > "${stage_dir}/.perga-install-marker"

if [[ "$(uname -s)" == "Darwin" ]] && command -v xattr >/dev/null 2>&1; then
  xattr -dr com.apple.quarantine "$stage_dir" 2>/dev/null || true
fi

backup_dir="${parent_dir}/.perga-prev-$$"
rm -rf "$backup_dir"
if [[ -e "$INSTALL_DIR" ]]; then
  mv "$INSTALL_DIR" "$backup_dir"
fi
if ! mv "$stage_dir" "$INSTALL_DIR"; then
  if [[ -e "$backup_dir" ]]; then
    mv "$backup_dir" "$INSTALL_DIR" || true
  fi
  exit 1
fi
stage_dir=""
rm -rf "$backup_dir"

q_install_dir=$(quote_sh "$INSTALL_DIR")
q_data_dir=$(quote_sh "$DATA_DIR")

cat > "${BIN_DIR}/perga" <<EOF
#!/usr/bin/env bash
set -euo pipefail
DEFAULT_INSTALL_DIR=${q_install_dir}
DEFAULT_DATA_DIR=${q_data_dir}
INSTALL_DIR="\${PERGA_INSTALL_DIR:-\$DEFAULT_INSTALL_DIR}"
export PERGA_DATA_DIR="\${PERGA_DATA_DIR:-\$DEFAULT_DATA_DIR}"
export PERGA_STATIC_WEB_DIR="\${PERGA_STATIC_WEB_DIR:-\$INSTALL_DIR/web}"
export PERGA_TEMPLATES_DIR="\${PERGA_TEMPLATES_DIR:-\$INSTALL_DIR/templates}"

has_urls_override() {
  if [[ -n "\${ASPNETCORE_URLS:-}" || -n "\${Kestrel__Endpoints__Http__Url:-}" ]]; then
    return 0
  fi
  for arg in "\$@"; do
    case "\$arg" in
      --urls|--urls=*) return 0 ;;
    esac
  done
  return 1
}

has_content_root_override() {
  if [[ -n "\${ASPNETCORE_CONTENTROOT:-}" || -n "\${DOTNET_CONTENTROOT:-}" ]]; then
    return 0
  fi
  for arg in "\$@"; do
    case "\$arg" in
      --contentRoot|--contentRoot=*) return 0 ;;
    esac
  done
  return 1
}

run_host() {
  local -a extra_args=()
  if ! has_content_root_override "\$@"; then
    extra_args+=(--contentRoot "\$INSTALL_DIR")
  fi
  if ! has_urls_override "\$@"; then
    extra_args+=(--urls "\${PERGA_URLS:-http://127.0.0.1:4041}")
  fi
  # Anchor working directory and content root to the installed package so a
  # caller's project appsettings.json or templates/ cannot hijack Host startup.
  cd "\$INSTALL_DIR"
  exec "\$INSTALL_DIR/Perga.Host" "\${extra_args[@]}" "\$@"
}

case "\${1:-}" in
  -h|--help|help)
    cat <<'USAGE'
Usage:
  perga [host args...]               Start Perga on http://127.0.0.1:4041 (override with --urls or PERGA_URLS)
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
    run_host "\$@"
    ;;
  *)
    run_host "\$@"
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
