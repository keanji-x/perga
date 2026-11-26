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

if [[ ! "$TAG" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "error: tag '$TAG' must match vX.Y.Z or vX.Y.Z-prerelease (for example v0.1.0 or v0.1.0-rc.1)." >&2
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

if [[ -n "$(git ls-remote --tags origin "refs/tags/${TAG}")" ]]; then
  echo "error: tag '${TAG}' already exists on origin." >&2
  exit 1
fi

if (( SKIP_CI_CHECK == 0 )); then
  if ! command -v gh >/dev/null 2>&1; then
    echo "error: 'gh' CLI is required to verify CI status (or pass --skip-ci-check)." >&2
    exit 1
  fi
  ci_conclusion=$(gh run list --workflow=ci.yml --commit "$HEAD_SHA" --limit 10 \
    --json conclusion --jq 'map(.conclusion) | if any(. == "success") then "success" else (.[0] // "none") end')
  if [[ "$ci_conclusion" != "success" ]]; then
    echo "error: CI and release workflow has not passed for ${HEAD_SHA:0:8} (latest conclusion: ${ci_conclusion})." >&2
    echo "Wait for CI on main to finish or pass --skip-ci-check if you have verified it manually." >&2
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
