#!/usr/bin/env bash
# Lock Nightly vs stable tag selection. Uses a fake gh and a fixed clock.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RESOLVE="$ROOT/scripts/resolve-release.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

mkdir -p "$TMP/bin"
cat > "$TMP/bin/gh" << 'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [[ "${1:-}" == "release" && "${2:-}" == "view" ]]; then
  name="${3:-}"
  case ",${GH_EXISTING_RELEASES:-}," in
    *",${name},"*) exit 0 ;;
  esac
  exit 1
fi
echo "unexpected gh invocation: $*" >&2
exit 2
EOF
chmod +x "$TMP/bin/gh"

NOW="$(date -d '2026-10-23 02:00:00 UTC' +%s)"          # 2026-10-23 10:00 Asia/Shanghai
COMMIT_TODAY="$(date -d '2026-10-22 16:30:00 UTC' +%s)" # 2026-10-23 00:30 Asia/Shanghai
COMMIT_YESTERDAY="$(date -d '2026-10-22 15:00:00 UTC' +%s)" # 2026-10-22 23:00 Asia/Shanghai
SHA="abcdef0123456789abcdef0123456789abcdef01"

failures=0
assert_eq() {
  local file="$1" key="$2" expected="$3" actual
  actual="$(grep "^${key}=" "$file" | tail -n 1 | cut -d= -f2-)"
  if [[ "$actual" != "$expected" ]]; then
    echo "FAIL ${key}: expected [${expected}] got [${actual}]" >&2
    failures=$((failures + 1))
  fi
}

run_resolve() {
  local out="$1"
  shift
  : > "$out"
  env PATH="$TMP/bin:$PATH" \
    GITHUB_OUTPUT="$out" \
    GITHUB_REPOSITORY="hdward-dev/asterdock" \
    GITHUB_SHA="$SHA" \
    RESOLVE_NOW_EPOCH="$NOW" \
    "$@" \
    bash "$RESOLVE" > "$out.log" 2>&1
}

expect_ok() {
  local name="$1"
  shift
  local out="$TMP/${name}.env"
  if ! run_resolve "$out" "$@"; then
    echo "FAIL ${name}: resolver exited $?" >&2
    cat "$out.log" >&2 || true
    failures=$((failures + 1))
    return
  fi
  echo "ok ${name}"
  LAST_OUT="$out"
}

expect_fail() {
  local name="$1"
  shift
  local out="$TMP/${name}.env"
  if run_resolve "$out" "$@"; then
    echo "FAIL ${name}: expected a non-zero exit" >&2
    failures=$((failures + 1))
    return
  fi
  echo "ok ${name}"
}

expect_ok stable-new \
  GITHUB_EVENT_NAME=push \
  GITHUB_REF_NAME=v1.0.70 \
  GITHUB_RUN_NUMBER=1 \
  RESOLVE_COMMIT_EPOCH="$COMMIT_YESTERDAY"
assert_eq "$LAST_OUT" channel stable
assert_eq "$LAST_OUT" version 1.0.70
assert_eq "$LAST_OUT" tag v1.0.70
assert_eq "$LAST_OUT" should_release true
assert_eq "$LAST_OUT" release_date 2026-10-23

expect_ok stable-exists \
  GITHUB_EVENT_NAME=push \
  GITHUB_REF_NAME=v1.0.69 \
  GITHUB_RUN_NUMBER=999 \
  GH_EXISTING_RELEASES=v1.0.69 \
  RESOLVE_COMMIT_EPOCH="$COMMIT_TODAY"
assert_eq "$LAST_OUT" channel stable
assert_eq "$LAST_OUT" should_release false
assert_eq "$LAST_OUT" version 1.0.69

expect_ok nightly-schedule-today \
  GITHUB_EVENT_NAME=schedule \
  GITHUB_REF_NAME=main \
  GITHUB_RUN_NUMBER=80 \
  RESOLVE_COMMIT_EPOCH="$COMMIT_TODAY"
assert_eq "$LAST_OUT" channel nightly
assert_eq "$LAST_OUT" version 1.0.80
assert_eq "$LAST_OUT" tag v1.0.80-nightly-20261023-abcdef0
assert_eq "$LAST_OUT" should_release true
parsed="$(grep '^tag=' "$LAST_OUT" | cut -d= -f2-)"
parsed="${parsed#v}"
parsed="${parsed%%-*}"
if [[ "$parsed" != "1.0.80" ]]; then
  echo "FAIL nightly tag prefix: ${parsed}" >&2
  failures=$((failures + 1))
fi

expect_ok nightly-schedule-quiet-day \
  GITHUB_EVENT_NAME=schedule \
  GITHUB_REF_NAME=main \
  GITHUB_RUN_NUMBER=81 \
  RESOLVE_COMMIT_EPOCH="$COMMIT_YESTERDAY"
assert_eq "$LAST_OUT" channel nightly
assert_eq "$LAST_OUT" should_release false
assert_eq "$LAST_OUT" tag v1.0.81-nightly-20261023-abcdef0

expect_ok nightly-dispatch-old-commit \
  GITHUB_EVENT_NAME=workflow_dispatch \
  GITHUB_REF_NAME=v1.0.70 \
  GITHUB_RUN_NUMBER=82 \
  RESOLVE_COMMIT_EPOCH="$COMMIT_YESTERDAY"
assert_eq "$LAST_OUT" channel nightly
assert_eq "$LAST_OUT" version 1.0.82
assert_eq "$LAST_OUT" tag v1.0.82-nightly-20261023-abcdef0
assert_eq "$LAST_OUT" should_release true

expect_ok nightly-legacy-tag-exists \
  GITHUB_EVENT_NAME=schedule \
  GITHUB_REF_NAME=main \
  GITHUB_RUN_NUMBER=83 \
  GH_EXISTING_RELEASES="nightly-20261023-abcdef0" \
  RESOLVE_COMMIT_EPOCH="$COMMIT_TODAY"
assert_eq "$LAST_OUT" should_release false

expect_ok nightly-current-tag-exists \
  GITHUB_EVENT_NAME=workflow_dispatch \
  GITHUB_REF_NAME=main \
  GITHUB_RUN_NUMBER=84 \
  GH_EXISTING_RELEASES="v1.0.84-nightly-20261023-abcdef0" \
  RESOLVE_COMMIT_EPOCH="$COMMIT_YESTERDAY"
assert_eq "$LAST_OUT" should_release false

expect_fail push-nightly-tag \
  GITHUB_EVENT_NAME=push \
  GITHUB_REF_NAME=v1.0.68-nightly-20261015-2d70abd \
  GITHUB_RUN_NUMBER=85 \
  RESOLVE_COMMIT_EPOCH="$COMMIT_TODAY"

expect_fail push-prerelease \
  GITHUB_EVENT_NAME=push \
  GITHUB_REF_NAME=v1.2.3-rc1 \
  GITHUB_RUN_NUMBER=86 \
  RESOLVE_COMMIT_EPOCH="$COMMIT_TODAY"

expect_fail push-short-tag \
  GITHUB_EVENT_NAME=push \
  GITHUB_REF_NAME=v1.2 \
  GITHUB_RUN_NUMBER=87 \
  RESOLVE_COMMIT_EPOCH="$COMMIT_TODAY"

expect_fail pull-request \
  GITHUB_EVENT_NAME=pull_request \
  GITHUB_REF_NAME=main \
  GITHUB_RUN_NUMBER=88 \
  RESOLVE_COMMIT_EPOCH="$COMMIT_TODAY"

if [[ "$failures" -ne 0 ]]; then
  echo "${failures} assertion(s) failed" >&2
  exit 1
fi
echo "resolve-release: all cases passed"
