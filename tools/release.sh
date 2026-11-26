#!/usr/bin/env bash
# Tag origin/main as a Perga release. Runs checks only unless --push is given; CI publishes the tag.
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: bash tools/release.sh vX.Y.Z[-prerelease] [--push] [--skip-ci-check]

Checks that the tag is a new strict SemVer tag, the tracked working tree is clean,
HEAD is origin/main, and the latest push run of ci.yml on main for HEAD succeeded.

  --push           Create the annotated tag and push it to origin.
  --skip-ci-check  Do not require a successful CI run (needs no gh CLI).
USAGE
}

die() {
  echo "error: $*" >&2
  exit 1
}

TAG=""
PUSH=0
CHECK_CI=1
for arg in "$@"; do
  case "$arg" in
    -h|--help) usage; exit 0 ;;
    --push) PUSH=1 ;;
    --skip-ci-check) CHECK_CI=0 ;;
    -*) usage >&2; die "unknown option '${arg}'." ;;
    *)
      [[ -z "$TAG" ]] || die "expected a single tag, got '${TAG}' and '${arg}'."
      TAG="$arg"
      ;;
  esac
done
if [[ -z "$TAG" ]]; then
  usage >&2
  exit 2
fi

ident='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
semver="^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-${ident}(\.${ident})*)?\$"
[[ "$TAG" =~ $semver ]] || die "'${TAG}' is not a strict SemVer tag such as v0.1.0 or v0.2.0-rc.1."
git check-ref-format "refs/tags/${TAG}" || die "'${TAG}' is not a valid Git tag name."

[[ -z "$(git status --porcelain --untracked-files=no)" ]] || die "tracked files have uncommitted changes."
git fetch origin main --tags --quiet
head=$(git rev-parse HEAD)
main=$(git rev-parse origin/main)
[[ "$head" == "$main" ]] || die "HEAD (${head}) is not origin/main (${main})."
if git rev-parse --quiet --verify "refs/tags/${TAG}" > /dev/null; then
  die "tag ${TAG} already exists locally."
fi
remote_tag=$(git ls-remote --tags origin "refs/tags/${TAG}") || die "could not list tags on origin."
[[ -z "$remote_tag" ]] || die "tag ${TAG} already exists on origin."

if (( CHECK_CI )); then
  command -v gh > /dev/null 2>&1 || die "the gh CLI is required to check CI; pass --skip-ci-check to skip."
  ci=$(gh run list --workflow ci.yml --branch main --event push --commit "$head" --limit 1 \
    --json status,conclusion --jq 'map("\(.status):\(.conclusion)") | first // "missing"') || die "could not read CI runs."
  [[ "$ci" == "completed:success" ]] || die "the latest ci.yml push run on main for ${head} is '${ci}', not completed:success."
fi

if (( ! PUSH )); then
  echo "Dry run passed: ${TAG} would tag ${head}. Re-run with --push to publish."
  exit 0
fi
git tag --annotate "$TAG" --message "Perga ${TAG}" "$head"
git push origin "refs/tags/${TAG}"
echo "Pushed ${TAG} (${head}). CI publishes the GitHub Release from this tag."
