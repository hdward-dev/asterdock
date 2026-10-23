#!/usr/bin/env bash
# Decide whether the Release workflow should publish, and which channel.
#
# Nightly and formal releases share .github/workflows/release.yml on purpose.
# Nightly versions are 1.0.<github.run_number> for that file. Moving the
# schedule to a new workflow file would restart the counter below v1.0.69.
#
# Environment (set by GitHub Actions):
#   GITHUB_EVENT_NAME   push (clean semver tag) | schedule | workflow_dispatch
#   GITHUB_REF_NAME     tag or branch name
#   GITHUB_RUN_NUMBER   used only for Nightly
#   GITHUB_SHA          used only for the Nightly tag suffix
#   GITHUB_REPOSITORY
#   GITHUB_OUTPUT
#
# Test-only clock overrides (the workflow must not set these):
#   RESOLVE_NOW_EPOCH
#   RESOLVE_COMMIT_EPOCH
#
# Outputs: channel (nightly|stable), version, tag, release_date (YYYY-MM-DD),
# should_release (true|false).
set -euo pipefail

: "${GITHUB_OUTPUT:?GITHUB_OUTPUT is required}"
: "${GITHUB_EVENT_NAME:?GITHUB_EVENT_NAME is required}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"

if [[ -n "${RESOLVE_NOW_EPOCH:-}" ]]; then
  now_epoch="$RESOLVE_NOW_EPOCH"
else
  now_epoch="$(date +%s)"
fi
if [[ -n "${RESOLVE_COMMIT_EPOCH:-}" ]]; then
  commit_timestamp="$RESOLVE_COMMIT_EPOCH"
else
  commit_timestamp="$(git log -1 --format=%ct)"
fi

release_date="$(TZ=Asia/Shanghai date -d "@$now_epoch" +%Y%m%d)"
display_date="$(TZ=Asia/Shanghai date -d "@$now_epoch" +%Y-%m-%d)"
commit_date="$(TZ=Asia/Shanghai date -d "@$commit_timestamp" +%Y%m%d)"
should_release="false"
channel=""
version=""
tag=""

release_exists() {
  gh release view "$1" --repo "$GITHUB_REPOSITORY" >/dev/null 2>&1
}

if [[ "$GITHUB_EVENT_NAME" == "push" && "${GITHUB_REF_NAME:-}" =~ ^v([0-9]+\.[0-9]+\.[0-9]+)$ ]]; then
  # Clean tags have no hyphen, so clients keep the full numeric version.
  channel="stable"
  version="${BASH_REMATCH[1]}"
  tag="v${version}"
  should_release="true"
  if release_exists "$tag"; then
    echo "Release ${tag} already exists; skipping."
    should_release="false"
  fi
elif [[ "$GITHUB_EVENT_NAME" == "workflow_dispatch" || "$GITHUB_EVENT_NAME" == "schedule" ]]; then
  : "${GITHUB_RUN_NUMBER:?GITHUB_RUN_NUMBER is required for Nightly}"
  : "${GITHUB_SHA:?GITHUB_SHA is required for Nightly}"
  channel="nightly"
  version="1.0.${GITHUB_RUN_NUMBER}"
  short_sha="${GITHUB_SHA:0:7}"
  # Existing clients parse the numeric prefix before the first hyphen.
  tag="v${version}-nightly-${release_date}-${short_sha}"
  if [[ "$GITHUB_EVENT_NAME" == "workflow_dispatch" || "$commit_date" == "$release_date" ]]; then
    should_release="true"
  else
    echo "No commit on ${display_date} Asia/Shanghai; skipping Nightly."
  fi
  if release_exists "$tag" || release_exists "nightly-${release_date}-${short_sha}"; then
    echo "Nightly release already exists; skipping."
    should_release="false"
  fi
else
  echo "Refusing to publish: event=${GITHUB_EVENT_NAME} ref=${GITHUB_REF_NAME:-<empty>}." >&2
  echo "Formal releases are a push of vMAJOR.MINOR.PATCH. Nightly is schedule or workflow_dispatch." >&2
  exit 1
fi

{
  echo "channel=${channel}"
  echo "should_release=${should_release}"
  echo "release_date=${display_date}"
  echo "tag=${tag}"
  echo "version=${version}"
} >> "$GITHUB_OUTPUT"

echo "channel=${channel} version=${version} tag=${tag} should_release=${should_release} release_date=${display_date}"
