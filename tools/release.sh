#!/usr/bin/env bash
# Cut and optionally push a verified release tag (vX.Y.Z or vX.Y.Z-prerelease).
# Defaults to dry-run; pass --push to create and push the annotated tag.
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage:
  bash tools/release.sh <tag> [--push] [--skip-ci-check]

Examples:
  bash tools/release.sh v0.1.0               # Dry-run: verify main, tag uniqueness, and green CI
  bash tools/release.sh v0.1.0 --push        # Create annotated tag and push to origin
  bash tools/release.sh v0.1.0-rc.1 --push   # Cut a prerelease tag
USAGE
}

if (( $# < 1 )); then
  usage >&2
  exit 1
fi

TAG=""
DO_PUSH=0
SKIP_CI_CHECK=0

while (( $# > 0 )); do
  case "$1" in
    -h|--help)
      usage
      exit 0
      ;;
    --push)
      DO_PUSH=1
      ;;
    --skip-ci-check)
      SKIP_CI_CHECK=1
      ;;
    -*)
      echo "error: unknown option '$1'." >&2
      usage >&2
      exit 1
      ;;
    *)
      if [[ -n "$TAG" ]]; then
        echo "error: unexpected extra argument '$1'." >&2
        usage >&2
        exit 1
      fi
      TAG="$1"
      ;;
  esac
  shift
done

SEMVER_RE='^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?$'
if [[ ! "$TAG" =~ $SEMVER_RE ]] || ! git check-ref-format "refs/tags/${TAG}" >/dev/null 2>&1; then
  echo "error: tag '$TAG' must be a valid SemVer Git tag (for example v0.1.0 or v0.1.0-rc.1)." >&2
  exit 1
fi

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

if ! git diff --quiet || ! git diff --cached --quiet; then
  echo "error: tracked files have uncommitted changes. Commit or stash them before releasing." >&2
  exit 1
fi

git fetch origin main --tags --quiet

HEAD_SHA=$(git rev-parse HEAD)
MAIN_SHA=$(git rev-parse origin/main)
if [[ "$HEAD_SHA" != "$MAIN_SHA" ]]; then
  echo "error: HEAD (${HEAD_SHA:0:8}) does not match origin/main (${MAIN_SHA:0:8})." >&2
  echo "Run 'git checkout origin/main' or sync your main branch before tagging." >&2
  exit 1
fi

if git rev-parse -q --verify "refs/tags/${TAG}" >/dev/null; then
  echo "error: tag '${TAG}' already exists locally." >&2
  exit 1
fi

remote_tag=$(git ls-remote --tags origin "refs/tags/${TAG}") || {
  echo "error: failed to query remote tags on origin." >&2
  exit 1
}
if [[ -n "$remote_tag" ]]; then
  echo "error: tag '${TAG}' already exists on origin." >&2
  exit 1
fi

if (( SKIP_CI_CHECK == 0 )); then
  if ! command -v gh >/dev/null 2>&1; then
    echo "error: 'gh' CLI is required to verify CI status (or pass --skip-ci-check)." >&2
    exit 1
  fi
  ci_latest=$(gh run list --workflow=ci.yml --branch main --event push --commit "$HEAD_SHA" --limit 20 \
    --json status,conclusion,createdAt \
    --jq 'sort_by(.createdAt) | reverse | if length == 0 then "none:none" else "\(.[0].status):\(.[0].conclusion // "none")" end')
  if [[ "$ci_latest" != "completed:success" ]]; then
    echo "error: latest push CI and release run on main for ${HEAD_SHA:0:8} has not succeeded (latest: ${ci_latest})." >&2
    echo "Wait for CI on main to pass or pass --skip-ci-check if you have verified it manually." >&2
    exit 1
  fi
fi

release_kind="stable release"
if [[ "$TAG" == *-* ]]; then
  release_kind="prerelease"
fi

echo "Release check passed:"
echo "  Tag:     ${TAG} (${release_kind})"
echo "  Commit:  $(git log -1 --oneline "$HEAD_SHA")"

if (( DO_PUSH == 0 )); then
  echo ""
  echo "Dry-run only. To create and push this tag, run:"
  echo "  bash tools/release.sh ${TAG} --push"
  exit 0
fi

git tag -a "$TAG" -m "Release ${TAG}" "$HEAD_SHA"
git push origin "refs/tags/${TAG}"

echo ""
echo "Pushed ${TAG}. GitHub Actions will build, smoke-test, and publish the release:"
if command -v gh >/dev/null 2>&1; then
  repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner 2>/dev/null || true)
  if [[ -n "$repo" ]]; then
    echo "  https://github.com/${repo}/actions/workflows/ci.yml"
    echo "  https://github.com/${repo}/releases/tag/${TAG}"
  fi
fi
