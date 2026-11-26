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

normalize_link_target() {
  local p="$1"
  while [[ "$p" != "/" ]]; do
    if [[ "$p" == */ ]]; then
      p="${p%/}"
    elif [[ "$p" == */. ]]; then
      p="${p%/.}"
    else
      break
    fi
  done
  printf '%s' "${p:-/}"
}

need_cmd curl
need_cmd tar

if [[ -L "$(normalize_link_target "$RAW_INSTALL_DIR")" ]]; then
  echo "error: PERGA_INSTALL_DIR (${RAW_INSTALL_DIR}) must be a real directory, not a symlink." >&2
  exit 1
fi

INSTALL_DIR=$(resolve_physical_path "$RAW_INSTALL_DIR" "PERGA_INSTALL_DIR")
BIN_DIR=$(resolve_physical_path "$RAW_BIN_DIR" "PERGA_BIN_DIR")
DATA_DIR=$(resolve_physical_path "$RAW_DATA_DIR" "PERGA_DATA_DIR")
DB_PATH=""
if [[ -n "${PERGA_DB_PATH:-}" ]]; then
  DB_PATH=$(resolve_physical_path "$PERGA_DB_PATH" "PERGA_DB_PATH")
fi

if [[ "$DATA_DIR" == "$INSTALL_DIR" || "$DATA_DIR" == "${INSTALL_DIR}/"* ]]; then
  echo "error: PERGA_DATA_DIR (${DATA_DIR}) must not be inside PERGA_INSTALL_DIR (${INSTALL_DIR}), or upgrades would overwrite your data." >&2
  exit 1
fi

if [[ -n "$DB_PATH" ]] && [[ "$DB_PATH" == "$INSTALL_DIR" || "$DB_PATH" == "${INSTALL_DIR}/"* ]]; then
  echo "error: PERGA_DB_PATH (${DB_PATH}) must not be inside PERGA_INSTALL_DIR (${INSTALL_DIR}), or upgrades would overwrite your database." >&2
  exit 1
fi

if [[ -e "$INSTALL_DIR" && ! -d "$INSTALL_DIR" ]]; then
  echo "error: PERGA_INSTALL_DIR (${INSTALL_DIR}) exists and is not a directory." >&2
  exit 1
fi

if [[ -d "$INSTALL_DIR" ]] && [[ -n "$(ls -A "$INSTALL_DIR")" ]]; then
  is_existing_perga=0
  if [[ -f "${INSTALL_DIR}/.perga-install-marker" ]]; then
    is_existing_perga=1
  elif [[ -f "${INSTALL_DIR}/Perga.Host" && -x "${INSTALL_DIR}/Perga.Host" && -f "${INSTALL_DIR}/web/index.html" && -f "${INSTALL_DIR}/oar-bridge/perga-oar-bridge.mjs" ]]; then
    is_existing_perga=1
  fi
  if (( is_existing_perga == 0 )); then
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

previous_version=""
if [[ -f "${INSTALL_DIR}/VERSION" ]]; then
  previous_version=$(head -n 1 "${INSTALL_DIR}/VERSION" 2>/dev/null | tr -d '\r\n' || true)
fi

echo "Extracting into ${INSTALL_DIR}..."
parent_dir=$(dirname "$INSTALL_DIR")
mkdir -p "$parent_dir" "$BIN_DIR" "$DATA_DIR"
stage_dir=$(mktemp -d "${parent_dir}/.perga-stage.XXXXXX")
tar -xzf "${tmp}/${ARCHIVE}" -C "${tmp}"
if [[ ! -f "${tmp}/perga/Perga.Host" || ! -x "${tmp}/perga/Perga.Host" ]]; then
  echo "error: archive did not contain an executable perga/Perga.Host binary." >&2
  exit 1
fi
rmdir "$stage_dir"
mv "${tmp}/perga" "$stage_dir"
printf 'perga-install-v1\n' > "${stage_dir}/.perga-install-marker"

installed_version="$VERSION"
if [[ -f "${stage_dir}/VERSION" ]]; then
  pkg_version=$(head -n 1 "${stage_dir}/VERSION" 2>/dev/null | tr -d '\r\n' || true)
  if [[ -n "$pkg_version" ]]; then
    installed_version="$pkg_version"
  fi
fi

if [[ "$(uname -s)" == "Darwin" ]] && command -v xattr >/dev/null 2>&1; then
  xattr -dr com.apple.quarantine "$stage_dir" 2>/dev/null || true
fi

was_upgrade=0
backup_dir="${parent_dir}/.perga-prev-$$"
rm -rf "$backup_dir"
if [[ -e "$INSTALL_DIR" ]]; then
  was_upgrade=1
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
q_bin_dir=$(quote_sh "$BIN_DIR")
q_data_dir=$(quote_sh "$DATA_DIR")
q_repo=$(quote_sh "$REPO")

cat > "${BIN_DIR}/perga" <<EOF
#!/usr/bin/env bash
set -euo pipefail
CALLER_PWD="\$(pwd -P)"
abs_from_caller() {
  case "\$1" in
    /*) printf '%s' "\$1" ;;
    *)  printf '%s/%s' "\$CALLER_PWD" "\$1" ;;
  esac
}
normalize_link_target() {
  local p="\$1"
  while [[ "\$p" != "/" ]]; do
    if [[ "\$p" == */ ]]; then
      p="\${p%/}"
    elif [[ "\$p" == */. ]]; then
      p="\${p%/.}"
    else
      break
    fi
  done
  printf '%s' "\${p:-/}"
}
resolve_physical_path() {
  local raw="\$1"
  local name="\$2"
  if [[ -z "\$raw" ]]; then
    echo "error: \${name} cannot be empty." >&2
    exit 1
  fi
  local path="\$(abs_from_caller "\$raw")"
  local resolved="/"
  local missing_tail=0
  local IFS='/'
  local -a segments=()
  read -r -a segments <<< "\$path"
  local seg candidate parent_phys
  for seg in "\${segments[@]}"; do
    if [[ -z "\$seg" || "\$seg" == "." ]]; then
      continue
    fi
    if [[ "\$seg" == ".." ]]; then
      if [[ "\$resolved" != "/" ]]; then
        resolved="\${resolved%/*}"
        [[ -z "\$resolved" ]] && resolved="/"
      fi
      if [[ -d "\$resolved" ]]; then
        resolved=\$(cd -P -- "\$resolved" && pwd -P)
        missing_tail=0
      fi
      continue
    fi
    if [[ "\$resolved" == "/" ]]; then
      candidate="/\${seg}"
    else
      candidate="\${resolved}/\${seg}"
    fi
    if (( missing_tail == 0 )) && [[ -d "\$candidate" ]]; then
      resolved=\$(cd -P -- "\$candidate" && pwd -P)
    elif (( missing_tail == 0 )) && [[ -L "\$candidate" ]]; then
      parent_phys=\$(cd -P -- "\$resolved" && pwd -P)
      if [[ "\$parent_phys" == "/" ]]; then
        resolved="/\${seg}"
      else
        resolved="\${parent_phys}/\${seg}"
      fi
      missing_tail=1
    else
      resolved="\$candidate"
      missing_tail=1
    fi
  done
  printf '%s' "\$resolved"
}
DEFAULT_INSTALL_DIR=${q_install_dir}
DEFAULT_BIN_DIR=${q_bin_dir}
DEFAULT_DATA_DIR=${q_data_dir}
DEFAULT_REPO=${q_repo}
RAW_INSTALL_DIR="\${PERGA_INSTALL_DIR:-\$DEFAULT_INSTALL_DIR}"
INSTALL_DIR="\$(abs_from_caller "\$RAW_INSTALL_DIR")"
BIN_DIR="\$(abs_from_caller "\${PERGA_BIN_DIR:-\$DEFAULT_BIN_DIR}")"
REPO="\${PERGA_REPO:-\$DEFAULT_REPO}"
export PERGA_DATA_DIR="\$(abs_from_caller "\${PERGA_DATA_DIR:-\$DEFAULT_DATA_DIR}")"
export PERGA_STATIC_WEB_DIR="\$(abs_from_caller "\${PERGA_STATIC_WEB_DIR:-\$INSTALL_DIR/web}")"
export PERGA_TEMPLATES_DIR="\$(abs_from_caller "\${PERGA_TEMPLATES_DIR:-\$INSTALL_DIR/templates}")"
if [[ -n "\${PERGA_DB_PATH:-}" ]]; then
  export PERGA_DB_PATH="\$(abs_from_caller "\$PERGA_DB_PATH")"
fi
EFFECTIVE_DB_PATH="\${PERGA_DB_PATH:-\$PERGA_DATA_DIR/perga.db}"

read_installed_version() {
  if [[ -f "\$INSTALL_DIR/VERSION" ]]; then
    local v
    v=\$(head -n 1 "\$INSTALL_DIR/VERSION" 2>/dev/null | tr -d '\r\n' || true)
    if [[ -n "\$v" ]]; then
      printf '%s' "\$v"
      return
    fi
  fi
  printf 'unknown'
}

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

run_upgrade() {
  if [[ "\${1:-}" == "-h" || "\${1:-}" == "--help" ]]; then
    cat <<'UPGRADE_USAGE'
Usage:
  perga upgrade [version]

Examples:
  perga upgrade           Upgrade to the latest stable GitHub Release
  perga upgrade v0.1.0    Install or pin a specific release tag

Your data directory (\$PERGA_DATA_DIR) is preserved across upgrades.
UPGRADE_USAGE
    exit 0
  fi
  if (( \$# > 1 )); then
    echo "error: 'perga upgrade' accepts at most one version argument." >&2
    exit 1
  fi
  local target_version="\${1:-\${PERGA_VERSION:-latest}}"
  if [[ "\$target_version" == -* ]]; then
    echo "error: unrecognized option '\$target_version' for 'perga upgrade'." >&2
    exit 1
  fi
  if ! command -v curl >/dev/null 2>&1; then
    echo "error: 'curl' is required to run 'perga upgrade'." >&2
    exit 1
  fi
  local default_installer_url="https://keanji-x.github.io/perga/install.sh"
  if [[ "\$REPO" != "keanji-x/perga" ]]; then
    default_installer_url="https://raw.githubusercontent.com/\${REPO}/main/install.sh"
  fi
  local installer_url="\${PERGA_INSTALLER_URL:-\$default_installer_url}"
  _PERGA_UPGRADE_TMP=\$(mktemp)
  trap 'rm -f "\${_PERGA_UPGRADE_TMP:-}"' EXIT
  curl --fail --silent --show-error --location --retry 3 --max-time 60 \\
    --output "\$_PERGA_UPGRADE_TMP" "\$installer_url"
  PERGA_REPO="\$REPO" \\
    PERGA_VERSION="\$target_version" \\
    PERGA_INSTALL_DIR="\$INSTALL_DIR" \\
    PERGA_BIN_DIR="\$BIN_DIR" \\
    PERGA_DATA_DIR="\$PERGA_DATA_DIR" \\
    bash "\$_PERGA_UPGRADE_TMP"
  rm -f "\$_PERGA_UPGRADE_TMP"
  _PERGA_UPGRADE_TMP=""
}

run_doctor() {
  if (( \$# > 0 )); then
    echo "error: 'perga doctor' does not accept arguments." >&2
    exit 1
  fi
  local ok=1
  echo "Perga version:     \$(read_installed_version)"
  echo "Install directory: \$INSTALL_DIR"
  echo "Launcher:          \$BIN_DIR/perga"
  echo "Data directory:    \$PERGA_DATA_DIR"
  if [[ -f "\$EFFECTIVE_DB_PATH" ]]; then
    echo "Database:          initialized (\$EFFECTIVE_DB_PATH)"
  else
    echo "Database:          not initialized yet (\$EFFECTIVE_DB_PATH; run: perga auth init-owner --username owner --allow-migrate)"
  fi

  local missing=()
  [[ -x "\$INSTALL_DIR/Perga.Host" ]] || missing+=("Perga.Host")
  [[ -x "\$INSTALL_DIR/perga-term-bridge" ]] || missing+=("perga-term-bridge")
  [[ -f "\$INSTALL_DIR/web/index.html" ]] || missing+=("web/index.html")
  [[ -f "\$INSTALL_DIR/oar-bridge/perga-oar-bridge.mjs" ]] || missing+=("oar-bridge/perga-oar-bridge.mjs")
  if (( \${#missing[@]} == 0 )); then
    echo "Package files:     ok"
  else
    echo "Package files:     missing (\${missing[*]})"
    ok=0
  fi

  if command -v node >/dev/null 2>&1; then
    local node_ver node_major
    node_ver=\$(node -v 2>/dev/null || echo unknown)
    node_major=\$(node -p 'Number(process.versions.node.split(".")[0])' 2>/dev/null || echo 0)
    if (( node_major >= 24 )); then
      echo "Node.js:           \$node_ver (ok)"
    else
      echo "Node.js:           \$node_ver (requires Node.js 24+)"
      ok=0
    fi
  else
    echo "Node.js:           not found (requires Node.js 24+)"
    ok=0
  fi

  local -a found_clis=()
  local cli
  for cli in claude codex agy antigravity opencode kimi qwen devin factory grok codebuddy qoder dsh; do
    if command -v "\$cli" >/dev/null 2>&1; then
      found_clis+=("\$cli")
    fi
  done
  if (( \${#found_clis[@]} > 0 )); then
    echo "Detected CLIs:     \${found_clis[*]}"
  else
    echo "Detected CLIs:     none found on PATH (install at least one, e.g. claude or codex)"
  fi

  (( ok == 1 ))
}

run_uninstall() {
  local yes=0
  while (( \$# > 0 )); do
    case "\$1" in
      -y|--yes) yes=1 ;;
      -h|--help)
        cat <<'UNINSTALL_USAGE'
Usage:
  perga uninstall [--yes]

Removes the Perga application directory and launcher.
Preserves your data directory (\$PERGA_DATA_DIR).
UNINSTALL_USAGE
        exit 0
        ;;
      *)
        echo "error: unrecognized option '\$1' for 'perga uninstall'." >&2
        exit 1
        ;;
    esac
    shift
  done

  local normalized_install_target
  normalized_install_target=\$(normalize_link_target "\$INSTALL_DIR")
  if [[ "\$normalized_install_target" == "/" || -z "\$INSTALL_DIR" || -L "\$normalized_install_target" ]]; then
    echo "error: refusing to uninstall invalid install directory '\$RAW_INSTALL_DIR'." >&2
    exit 1
  fi
  if [[ ! -d "\$normalized_install_target" || ! -f "\$normalized_install_target/.perga-install-marker" ]]; then
    echo "error: '\$RAW_INSTALL_DIR' does not contain a Perga installation marker; refusing to delete it." >&2
    exit 1
  fi
  local real_install_dir
  real_install_dir=\$(resolve_physical_path "\$normalized_install_target" "PERGA_INSTALL_DIR")
  if [[ "\$real_install_dir" == "/" ]]; then
    echo "error: refusing to uninstall '/'." >&2
    exit 1
  fi
  local real_default_install_dir
  real_default_install_dir=\$(resolve_physical_path "\$DEFAULT_INSTALL_DIR" "DEFAULT_INSTALL_DIR")
  if [[ "\$real_install_dir" != "\$real_default_install_dir" ]]; then
    echo "error: this launcher manages '\$real_default_install_dir', not '\$real_install_dir'. Unset PERGA_INSTALL_DIR or run the launcher for '\$real_install_dir'." >&2
    exit 1
  fi
  local real_data_dir real_db_path
  real_data_dir=\$(resolve_physical_path "\$PERGA_DATA_DIR" "PERGA_DATA_DIR")
  real_db_path=\$(resolve_physical_path "\$EFFECTIVE_DB_PATH" "PERGA_DB_PATH")
  if [[ "\$real_data_dir" == "\$real_install_dir" || "\$real_data_dir" == "\${real_install_dir}/"* ]]; then
    echo "error: refusing to remove '\$real_install_dir' because PERGA_DATA_DIR ('\$real_data_dir') is inside it." >&2
    exit 1
  fi
  if [[ "\$real_db_path" == "\$real_install_dir" || "\$real_db_path" == "\${real_install_dir}/"* ]]; then
    echo "error: refusing to remove '\$real_install_dir' because PERGA_DB_PATH ('\$real_db_path') is inside it." >&2
    exit 1
  fi
  local launcher_path="\$BIN_DIR/perga"
  local remove_launcher=0
  if [[ -e "\$launcher_path" ]]; then
    local expected_owner="DEFAULT_INSTALL_DIR='\${real_install_dir//\'/\'\\\\\'\'}'"
    if [[ -f "\$launcher_path" && ! -L "\$launcher_path" ]] && grep -Fqx "\$expected_owner" "\$launcher_path" 2>/dev/null; then
      remove_launcher=1
    else
      echo "error: '\$launcher_path' does not belong to '\$real_install_dir'; refusing to uninstall." >&2
      exit 1
    fi
  fi

  if (( yes == 0 )); then
    if [[ ! -t 0 ]]; then
      echo "error: refusing to uninstall non-interactively without --yes." >&2
      exit 1
    fi
    printf 'Remove %s and %s? User data in %s will be kept. [y/N] ' "\$real_install_dir" "\$launcher_path" "\$PERGA_DATA_DIR"
    local reply
    read -r reply || true
    case "\$reply" in
      y|Y|yes|YES) ;;
      *) echo "Aborted."; exit 0 ;;
    esac
  fi

  rm -rf "\$real_install_dir"
  if (( remove_launcher == 1 )); then
    rm -f "\$launcher_path"
  fi
  echo "Removed Perga from \$real_install_dir."
  echo "User data preserved at \$PERGA_DATA_DIR (remove it manually if you want a full wipe)."
}

case "\${1:-}" in
  -h|--help|help)
    cat <<'USAGE'
Usage:
  perga [host args...]               Start Perga on http://127.0.0.1:4041 (override with --urls or PERGA_URLS)
  perga start [host args...]         Same as 'perga'
  perga auth <subcommand> [args...]  Manage owner auth (e.g. perga auth init-owner --username owner --allow-migrate)
  perga version                      Show installed version and paths
  perga upgrade [version]            Upgrade to the latest release (or a specific tag like v0.1.0)
  perga doctor                       Check installation, Node.js 24+, and available CLI providers
  perga uninstall [--yes]            Remove Perga binaries (preserves your data directory)
USAGE
    exit 0
    ;;
  -v|--version|version)
    echo "perga \$(read_installed_version)"
    echo "install: \$INSTALL_DIR"
    echo "data:    \$PERGA_DATA_DIR"
    exit 0
    ;;
  upgrade)
    shift
    run_upgrade "\$@"
    ;;
  doctor)
    shift
    run_doctor "\$@"
    ;;
  uninstall)
    shift
    run_uninstall "\$@"
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
if (( was_upgrade == 1 )) && [[ -n "$previous_version" ]]; then
  echo "Upgraded Perga (${previous_version} -> ${installed_version}) in ${INSTALL_DIR}"
elif (( was_upgrade == 1 )); then
  echo "Upgraded Perga (${installed_version}) in ${INSTALL_DIR}"
else
  echo "Installed Perga (${installed_version}) to ${INSTALL_DIR}"
fi
echo "Launcher:          ${BIN_DIR}/perga"
echo "Data directory:    ${DATA_DIR}"

if command -v node >/dev/null 2>&1; then
  node_major=$(node -p 'Number(process.versions.node.split(".")[0])' 2>/dev/null || echo 0)
  if (( node_major < 24 )); then
    echo ""
    echo "Note: Node.js 24+ is required for the bundled agent bridge (found $(node -v))."
  fi
else
  echo ""
  echo "Note: Node.js 24+ is required for the bundled agent bridge. Install Node.js 24+ and your CLI (claude / codex)."
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
effective_db="${DB_PATH:-${DATA_DIR}/perga.db}"
if [[ ! -f "$effective_db" ]]; then
  echo "Next steps (first run):"
  echo "  1. Create your owner account:"
  echo "     ${cmd} auth init-owner --username owner --allow-migrate"
  echo "  2. Start Perga:"
  echo "     ${cmd}"
  echo "  3. Open http://127.0.0.1:4041/ in your browser."
else
  echo "Existing data preserved at ${effective_db}."
  if (( was_upgrade == 1 )); then
    echo "If Perga is currently running, restart it to load ${installed_version}."
  fi
  echo "Start Perga with:"
  echo "  ${cmd}"
fi
