#!/usr/bin/env bash
# Install or upgrade the self-contained Perga release for macOS (Apple Silicon) or Linux x64.
if [ -z "${BASH_VERSION:-}" ]; then
  echo "error: Perga's installer requires bash. Run: curl -fsSL https://keanji-x.github.io/perga/install.sh | bash" >&2
  exit 1
fi
set -euo pipefail

REPO="${PERGA_REPO:-keanji-x/perga}"
VERSION="${PERGA_VERSION:-latest}"
SEMVER_IDENT='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
SEMVER_TAG="^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-${SEMVER_IDENT}(\.${SEMVER_IDENT})*)?\$"

die() { echo "error: $*" >&2; exit 1; }
need_cmd() { command -v "$1" >/dev/null 2>&1 || die "required command '$1' was not found on PATH."; }
# Print the absolute physical path for $1. Existing directories are resolved with `cd -P`
# (so macOS /var and /tmp become /private/...), and a missing tail is appended as written.
# Anything ambiguous fails closed instead of being interpreted.
resolve_safe_path() {
  local raw="$1" name="$2" path existing tail="" seg
  [[ -n "$raw" ]] || die "${name} cannot be empty."
  if [[ "$raw" == *[[:cntrl:]]* ]]; then die "${name} must not contain control characters."; fi
  if [[ "$raw" == /* ]]; then path="$raw"; else path="${PWD}/${raw}"; fi
  while [[ "$path" == */ ]]; do path="${path%/}"; done
  [[ -n "$path" ]] || die "refusing to use '/' for ${name}."
  [[ ! -L "$path" ]] || die "${name} (${raw}) must not be a symlink."
  existing="$path"
  while [[ -n "$existing" && ! -d "$existing" ]]; do
    [[ ! -L "$existing" ]] || die "${name} (${raw}) has a broken symlink at ${existing}."
    if [[ -e "$existing" && "$existing" != "$path" ]]; then die "${name} (${raw}): ${existing} is not a directory."; fi
    seg="${existing##*/}"
    if [[ "$seg" == "." || "$seg" == ".." ]]; then die "${name} (${raw}) must not contain '.' or '..' in the part that does not exist yet."; fi
    [[ -z "$seg" ]] || tail="/${seg}${tail}"
    existing="${existing%/*}"
  done
  existing=$(cd -P -- "${existing:-/}" && pwd -P)
  path="${existing%/}${tail}"
  [[ -n "$path" ]] || die "refusing to use '/' for ${name}."
  printf '%s' "$path"
}
quote_sh() { local s="$1" escaped="'\\''"; printf "'%s'" "${s//\'/$escaped}"; }
# Read a value pinned by an existing launcher (as written by quote_sh) without executing it.
launcher_default() {
  local line value apostrophe="'" re="^'([^']|'[\\]'')*'\$"
  [[ -f "$LAUNCHER" && ! -L "$LAUNCHER" ]] || return 0
  line=$(grep -m1 "^$1='" "$LAUNCHER" 2>/dev/null) || return 0
  value="${line#"$1"=}"
  [[ "$value" =~ $re ]] || return 0
  value="${value:1:${#value}-2}"
  printf '%s' "${value//\'\\\'\'/$apostrophe}"
}

CASE_INSENSITIVE=0
if [[ "$(uname -s)" == "Darwin" || "${PERGA_CASE_INSENSITIVE_PATHS:-0}" == "1" ]]; then CASE_INSENSITIVE=1; fi
is_inside() {
  local path="$1" dir="$2"
  if [[ "$CASE_INSENSITIVE" == "1" ]]; then
    path=$(printf '%s' "$path" | tr '[:upper:]' '[:lower:]')
    dir=$(printf '%s' "$dir" | tr '[:upper:]' '[:lower:]')
  fi
  [[ "$path" == "$dir" || "$path" == "$dir/"* ]]
}

need_cmd curl
need_cmd tar
if [[ ! "$REPO" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$ ]]; then die "PERGA_REPO must look like owner/repo (got '${REPO}')."; fi
if [[ "$VERSION" != "latest" && ! "$VERSION" =~ $SEMVER_TAG ]]; then die "PERGA_VERSION must be 'latest' or a SemVer tag such as v0.1.0 (got '${VERSION}')."; fi
INSTALL_DIR=$(resolve_safe_path "${PERGA_INSTALL_DIR-$HOME/.local/share/perga-app}" PERGA_INSTALL_DIR)
BIN_DIR=$(resolve_safe_path "${PERGA_BIN_DIR-$HOME/.local/bin}" PERGA_BIN_DIR)
LAUNCHER="${BIN_DIR}/perga"
# An upgrade keeps the data directory and database path the existing launcher pinned.
if [[ -n "${PERGA_DATA_DIR+set}" ]]; then raw_data_dir="$PERGA_DATA_DIR"; else raw_data_dir=$(launcher_default DEFAULT_DATA_DIR); raw_data_dir="${raw_data_dir:-$HOME/.local/share/perga}"; fi
DATA_DIR=$(resolve_safe_path "$raw_data_dir" PERGA_DATA_DIR)
if [[ -n "${PERGA_DB_PATH+set}" ]]; then raw_db_path="$PERGA_DB_PATH"; else raw_db_path=$(launcher_default DEFAULT_DB_PATH); fi
DB_PATH=""
if [[ -n "$raw_db_path" ]]; then DB_PATH=$(resolve_safe_path "$raw_db_path" PERGA_DB_PATH); fi
if is_inside "$BIN_DIR" "$INSTALL_DIR"; then die "PERGA_BIN_DIR (${BIN_DIR}) must not be inside PERGA_INSTALL_DIR (${INSTALL_DIR}), or upgrades would delete the launcher."; fi
if is_inside "$DATA_DIR" "$INSTALL_DIR"; then die "PERGA_DATA_DIR (${DATA_DIR}) must not be inside PERGA_INSTALL_DIR (${INSTALL_DIR}), or upgrades would delete your data."; fi
if [[ -n "$DB_PATH" ]] && is_inside "$DB_PATH" "$INSTALL_DIR"; then die "PERGA_DB_PATH (${DB_PATH}) must not be inside PERGA_INSTALL_DIR (${INSTALL_DIR}), or upgrades would delete your data."; fi
effective_db_path=$(resolve_safe_path "${DB_PATH:-${DATA_DIR}/perga.db}" "database path")
if is_inside "$effective_db_path" "$INSTALL_DIR"; then die "database path (${effective_db_path}) must not be inside PERGA_INSTALL_DIR (${INSTALL_DIR}), or upgrades would delete your data."; fi
for var in $(compgen -e || true); do
  case "$var" in [Pp][Ee][Rr][Gg][Aa]__*) ;; *) continue ;; esac
  lower=$(printf '%s' "$var" | tr '[:upper:]' '[:lower:]')
  case "$lower" in perga__dbpath|perga__datadir|perga__datadirectory) ;; *) continue ;; esac
  val="${!var-}"
  [[ -n "$val" ]] || continue
  if [[ "$val" == *[[:cntrl:]]* ]]; then die "${var} must not contain control characters."; fi
  [[ "$val" == /* ]] || die "${var} (${val}) must be an absolute path."
  cfg=$(resolve_safe_path "$val" "$var")
  if is_inside "$cfg" "$INSTALL_DIR"; then die "${var} (${cfg}) must not be inside PERGA_INSTALL_DIR (${INSTALL_DIR}), or upgrades would delete your data."; fi
  if [[ "$lower" != "perga__dbpath" ]]; then
    cfg_db=$(resolve_safe_path "${cfg}/perga.db" "${var} database path")
    if is_inside "$cfg_db" "$INSTALL_DIR"; then die "${var} database path (${cfg_db}) must not be inside PERGA_INSTALL_DIR (${INSTALL_DIR}), or upgrades would delete your data."; fi
  fi
done
if [[ -e "$INSTALL_DIR" && ! -d "$INSTALL_DIR" ]]; then die "PERGA_INSTALL_DIR (${INSTALL_DIR}) exists and is not a directory."; fi
if [[ -d "$INSTALL_DIR" && -n "$(ls -A "$INSTALL_DIR")" && ( ! -f "${INSTALL_DIR}/.perga-install-marker" || -L "${INSTALL_DIR}/.perga-install-marker" ) ]] &&
  [[ ! -f "${INSTALL_DIR}/Perga.Host" || ! -x "${INSTALL_DIR}/Perga.Host" || ! -f "${INSTALL_DIR}/web/index.html" || ! -f "${INSTALL_DIR}/oar-bridge/perga-oar-bridge.mjs" ]]; then
  die "PERGA_INSTALL_DIR (${INSTALL_DIR}) is a non-empty directory that is not a Perga installation. Choose a dedicated directory such as \$HOME/.local/share/perga-app."
fi
if [[ -d "$LAUNCHER" ]]; then die "launcher path ${LAUNCHER} is a directory."; fi
case "$(uname -sm)" in
  "Linux x86_64")
    if ! getconf GNU_LIBC_VERSION >/dev/null 2>&1 &&
       { (command -v ldd >/dev/null 2>&1 && { ldd --version 2>&1 || true; } | grep -i musl >/dev/null) ||
         [[ -e /lib/ld-musl-x86_64.so.1 && ! -e /lib64/ld-linux-x86-64.so.2 ]]; }; then
      die "Linux musl (for example Alpine Linux) is not supported. Perga's Linux x64 release requires glibc."
    fi
    PACKAGE="perga-linux-x64" ;;
  "Darwin arm64") PACKAGE="perga-darwin-arm64" ;;
  *) die "unsupported platform '$(uname -sm)'. Perga ships self-contained binaries for Linux x86_64 and macOS Apple Silicon (arm64)." ;;
esac
if [[ -n "${PERGA_BASE_URL:-}" ]]; then
  BASE_URL="${PERGA_BASE_URL%/}"
elif [[ "$VERSION" == "latest" ]]; then
  BASE_URL="https://github.com/${REPO}/releases/latest/download"
else
  BASE_URL="https://github.com/${REPO}/releases/download/${VERSION}"
fi
ARCHIVE="${PACKAGE}.tar.gz"
tmp=$(mktemp -d)
stage_dir=""
launcher_tmp=""
cleanup() {
  rm -rf "$tmp"
  if [[ -n "$stage_dir" ]]; then rm -rf "$stage_dir"; fi
  if [[ -n "$launcher_tmp" ]]; then rm -f "$launcher_tmp"; fi
}
trap cleanup EXIT
download() {
  if ! curl --fail --silent --show-error --location --retry 3 --max-time "$3" --output "$2" "$1"; then
    echo "error: failed to download $1." >&2
    if [[ -z "${PERGA_BASE_URL:-}" ]]; then
      echo "Check that GitHub Release '${VERSION}' of ${REPO} has ${ARCHIVE}: https://github.com/${REPO}/releases" >&2
    fi
    exit 1
  fi
}
echo "Downloading ${ARCHIVE} (${VERSION})..."
download "${BASE_URL}/${ARCHIVE}" "${tmp}/${ARCHIVE}" 300
download "${BASE_URL}/${ARCHIVE}.sha256" "${tmp}/${ARCHIVE}.sha256" 60
echo "Verifying SHA-256 checksum..."
expected_hash=$(awk -v target="$ARCHIVE" '
  NF >= 2 {
    file = $2
    sub(/^\*/, "", file)
    sub(/^\.\//, "", file)
    if (file == target) { print tolower($1); count++ }
  }
  END { if (count != 1) exit 1 }
' "${tmp}/${ARCHIVE}.sha256") || die "${ARCHIVE}.sha256 did not contain a unique entry for ${ARCHIVE}."
[[ "$expected_hash" =~ ^[0-9a-f]{64}$ ]] || die "invalid SHA-256 digest in ${ARCHIVE}.sha256."
if command -v sha256sum >/dev/null 2>&1; then
  actual_hash=$(sha256sum "${tmp}/${ARCHIVE}" | awk '{print tolower($1)}')
elif command -v shasum >/dev/null 2>&1; then
  actual_hash=$(shasum -a 256 "${tmp}/${ARCHIVE}" | awk '{print tolower($1)}')
else
  die "neither sha256sum nor shasum is installed."
fi
if [[ "$actual_hash" != "$expected_hash" ]]; then
  die "SHA-256 mismatch for ${ARCHIVE} (expected ${expected_hash}, got ${actual_hash})."
fi
echo "Extracting into ${INSTALL_DIR}..."
parent_dir=$(dirname "$INSTALL_DIR")
mkdir -p "$parent_dir" "$BIN_DIR" "$DATA_DIR"
tar -xzf "${tmp}/${ARCHIVE}" -C "$tmp"
if [[ ! -f "${tmp}/perga/Perga.Host" || ! -x "${tmp}/perga/Perga.Host" ]]; then
  die "archive did not contain an executable perga/Perga.Host binary."
fi
stage_dir=$(mktemp -d "${parent_dir}/.perga-stage.XXXXXX")
rmdir "$stage_dir"
mv "${tmp}/perga" "$stage_dir"
printf 'perga-install-v1\n' > "${stage_dir}/.perga-install-marker"
if [[ "$(uname -s)" == "Darwin" ]] && command -v xattr >/dev/null 2>&1; then
  xattr -dr com.apple.quarantine "$stage_dir" 2>/dev/null || true
fi
prev_version=$(head -n 1 "${INSTALL_DIR}/VERSION" 2>/dev/null | tr -d '[:space:]' || true)
new_version=$(head -n 1 "${stage_dir}/VERSION" 2>/dev/null | tr -d '[:space:]' || true)
backup_dir="${parent_dir}/.perga-prev.$$"
rm -rf "$backup_dir"
if [[ -e "$INSTALL_DIR" ]]; then
  mv "$INSTALL_DIR" "$backup_dir"
fi
if ! mv "$stage_dir" "$INSTALL_DIR"; then
  if [[ -e "$backup_dir" ]]; then mv "$backup_dir" "$INSTALL_DIR" || true; fi
  die "could not move the new release into ${INSTALL_DIR}."
fi
stage_dir=""
rm -rf "$backup_dir"
# Replace the launcher atomically so a running `perga upgrade` never reads a half-written script.
launcher_tmp=$(mktemp "${BIN_DIR}/.perga-launcher.XXXXXX")
{
  echo '#!/usr/bin/env bash'
  echo '# Generated by the Perga installer. Re-run it or `perga upgrade` to regenerate.'
  echo 'set -euo pipefail'
  printf 'DEFAULT_INSTALL_DIR=%s\n' "$(quote_sh "$INSTALL_DIR")"
  printf 'DEFAULT_BIN_DIR=%s\n' "$(quote_sh "$BIN_DIR")"
  printf 'DEFAULT_DATA_DIR=%s\n' "$(quote_sh "$DATA_DIR")"
  printf 'DEFAULT_DB_PATH=%s\n' "$(quote_sh "$DB_PATH")"
  printf 'DEFAULT_REPO=%s\n' "$(quote_sh "$REPO")"
  cat <<'LAUNCHER_EOF'
abs() { case "$1" in /*) printf '%s' "$1" ;; *) printf '%s/%s' "$PWD" "$1" ;; esac; }
has_arg() { local a; for a in "${@:2}"; do [[ "$a" == "$1" || "$a" == "$1="* ]] && return 0; done; return 1; }
INSTALL_DIR="$(abs "${PERGA_INSTALL_DIR:-$DEFAULT_INSTALL_DIR}")"
db_path="${PERGA_DB_PATH:-$DEFAULT_DB_PATH}"
export PERGA_INSTALL_DIR="$INSTALL_DIR" PERGA_MANAGED_INSTALL_DIR="$DEFAULT_INSTALL_DIR" PERGA_MANAGED_BIN_DIR="$DEFAULT_BIN_DIR"
export PERGA_BIN_DIR="$(abs "${PERGA_BIN_DIR:-$DEFAULT_BIN_DIR}")"
export PERGA_DATA_DIR="$(abs "${PERGA_DATA_DIR:-$DEFAULT_DATA_DIR}")"
export PERGA_REPO="${PERGA_REPO:-$DEFAULT_REPO}"
export PERGA_STATIC_WEB_DIR="$(abs "${PERGA_STATIC_WEB_DIR:-$INSTALL_DIR/web}")"
export PERGA_TEMPLATES_DIR="$(abs "${PERGA_TEMPLATES_DIR:-$INSTALL_DIR/templates}")"
if [[ -n "$db_path" ]]; then export PERGA_DB_PATH="$(abs "$db_path")"; fi
case "${1:-}" in
  uninstall) exec "$DEFAULT_INSTALL_DIR/Perga.Host" "$@" ;;
  auth|version|-v|--version|doctor|upgrade|help|-h|--help) exec "$INSTALL_DIR/Perga.Host" "$@" ;;
  start) shift ;;
esac
host_args=()
if ! has_arg --contentRoot "$@" && [[ -z "${ASPNETCORE_CONTENTROOT:-}${DOTNET_CONTENTROOT:-}" ]]; then
  host_args+=(--contentRoot "$INSTALL_DIR")
fi
if ! has_arg --urls "$@" && [[ -z "${ASPNETCORE_URLS:-}${Kestrel__Endpoints__Http__Url:-}" ]]; then
  host_args+=(--urls "${PERGA_URLS:-http://127.0.0.1:4041}")
fi
# Anchor the working directory so a caller's appsettings.json or templates/ cannot hijack startup.
cd "$INSTALL_DIR"
exec "$INSTALL_DIR/Perga.Host" ${host_args[@]+"${host_args[@]}"} "$@"
LAUNCHER_EOF
} > "$launcher_tmp"
chmod 755 "$launcher_tmp"
mv -f "$launcher_tmp" "$LAUNCHER"
launcher_tmp=""
echo ""
if [[ -n "$prev_version" && "$prev_version" == "$new_version" ]]; then
  echo "Reinstalled Perga ${new_version}"
elif [[ -n "$prev_version" ]]; then
  echo "Upgraded Perga: ${prev_version} -> ${new_version:-unknown}"
else
  echo "Installed Perga ${new_version:-unknown}"
fi
echo "Install directory: ${INSTALL_DIR}"
echo "Launcher:          ${LAUNCHER}"
echo "Data directory:    ${DATA_DIR}"
if [[ -n "$DB_PATH" ]]; then echo "Database:          ${DB_PATH}"; fi
case ":${PATH}:" in
  *":${BIN_DIR}:"*) cmd="perga" ;;
  *)
    echo ""; echo "Add ${BIN_DIR} to your PATH:"
    shell=$(basename "${SHELL:-sh}")
    export_line="export PATH=\"$BIN_DIR:\$PATH\""
    append_command=echo cmd="$LAUNCHER"
    if [[ ! "$BIN_DIR" =~ ^[A-Za-z0-9_./+:@-]+$ ]]; then
      export_line="export PATH=$(quote_sh "$BIN_DIR"):\"\$PATH\""
      append_command="printf '%s\n'"
      cmd=$(quote_sh "$LAUNCHER")
    fi
    case "$shell" in
      zsh|bash)
        rc="~/.${shell}rc"
        [[ "$shell" == bash && "$(uname -s)" == Darwin && -f "$HOME/.bash_profile" && ! -f "$HOME/.bashrc" ]] && rc="~/.bash_profile"
        printf '  %s %s >> %s && source %s\n' "$append_command" "$(quote_sh "$export_line")" "$rc" "$rc" ;;
      fish)
        fish_q="${BIN_DIR//\\/\\\\}"; escaped="\\'"
        fish_q="${fish_q//\'/$escaped}"
        printf "  fish_add_path '%s'\n" "$fish_q"
        [[ "$cmd" == "$LAUNCHER" ]] || cmd="'$fish_q/perga'" ;;
      *) printf '  %s\n' "$export_line" ;;
    esac ;;
esac
echo ""
if [[ ! -f "${DB_PATH:-${DATA_DIR}/perga.db}" ]]; then
  echo "Next steps (first run):"
  echo "  1. Check Node.js 24+ and your provider CLIs: ${cmd} doctor"
  echo "  2. Start Perga (prompts to create your owner account on first run): ${cmd}"
  echo "  3. Open http://127.0.0.1:4041/ in your browser."
else
  echo "Existing data preserved at ${DATA_DIR}. Start Perga with: ${cmd}"
  echo "Check Node.js 24+ and your provider CLIs with: ${cmd} doctor"
fi
