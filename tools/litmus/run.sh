#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Runs the litmus WebDAV compliance suites (basic, copymove, props, locks) against a running Kumbu² server and
# compares the failures with expected-failures.txt (WebDAV design §7). Starts nothing itself.
#
#   KUMBU_ADMIN_PASSWORD=… tools/litmus/run.sh http://127.0.0.1:8080 admin
#
# Signs in as the given user through the REST API, confirms the password, creates a vault and a WebDAV share with
# write access to it, and points litmus at /dav/<vault>/.litmus/: a dot-folder, so litmus's extension-less files land
# in the parking area and litmus tests the protocol layer, not the store's rules on what a file may be.
#
# With KUMBU_INVITATION set to the link `kumbu bootstrap-admin` printed, the user is created first by redeeming it.
# Passwords come from the environment, never from the command line, and are never printed. The share's password goes
# to litmus as an argument, the only way litmus takes one.
#
# Exits non-zero on a failure that isn't expected, and on an expected failure that now passes: the list is kept
# exact, so a fix shows up as well as a regression.
set -euo pipefail

if [[ $# -ne 2 || -z "${KUMBU_ADMIN_PASSWORD:-}" ]]; then
  echo "usage: KUMBU_ADMIN_PASSWORD=… [KUMBU_INVITATION=<bootstrap link>] $0 BASE_URL USERNAME" >&2
  exit 2
fi

base="${1%/}"
username="$2"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
expected_file="$here/expected-failures.txt"
suites="basic copymove props locks"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
jar="$work/cookies"

# A state-changing /api request: JSON body on stdin, the session cookie, and the Origin the CSRF check wants. A
# refusal's body (the server's reason, which names no secret) goes to stderr.
api() {
  local answer
  if ! answer="$(curl -sS --fail-with-body -b "$jar" -c "$jar" -H "Origin: $base" -H 'Content-Type: application/json' \
    --data-binary @- "$base/api$1")"; then
    echo "POST /api$1 failed: $answer" >&2
    return 1
  fi
  printf '%s\n' "$answer"
}

if [[ -n "${KUMBU_INVITATION:-}" ]]; then
  code="${KUMBU_INVITATION##*#}"
  CODE="$code" USERNAME="$username" jq -n '{code: env.CODE, username: env.USERNAME, password: env.KUMBU_ADMIN_PASSWORD}' \
    | api /join >/dev/null
  echo "Redeemed the invitation as $username" >&2
fi

USERNAME="$username" jq -n '{username: env.USERNAME, password: env.KUMBU_ADMIN_PASSWORD}' | api /session >/dev/null
jq -n '{password: env.KUMBU_ADMIN_PASSWORD}' | api /session/confirm >/dev/null

vault="litmus-$(date +%s)-$$"
VAULT="$vault" jq -n '{name: env.VAULT}' | api /vaults >/dev/null
VAULT="$vault" jq -n '{label: env.VAULT, vaults: {(env.VAULT): "write"}}' | api /dav-shares >"$work/share.json"
share_user="$(jq -r '.share.username' "$work/share.json")"
share_password="$(jq -r '.password' "$work/share.json")"
rm -f "$work/share.json"
echo "Vault $vault, share $share_user" >&2

# litmus makes its own litmus/ below the URL it is given, which must exist. The credentials go to curl on stdin.
printf 'user = "%s:%s"\n' "$share_user" "$share_password" \
  | curl -sS --fail-with-body -K - -X MKCOL "$base/dav/$vault/.litmus/" >/dev/null

# litmus exits non-zero whenever a test fails; the comparison below decides.
TESTS="$suites" litmus -k "$base/dav/$vault/.litmus/" "$share_user" "$share_password" | tee "$work/litmus.out" || true
unset share_password

# "-> running `props':" opens a suite, and "12. propget...... FAIL (…)" reports a test; litmus writes each test's
# number and name again before its result, and a warning can come in between, so a FAIL belongs to the last test
# named. A test is suite/number.name: names repeat within a suite (props runs propget after each step) and across
# suites (init, begin, finish), and litmus numbers its tests the same on every run.
awk '
  /^-> running `/ { suite = $3; gsub(/[`'"'"':]/, "", suite); next }
  {
    line = $0
    failed = sub(/ FAIL( .*)?$/, "", line)
    while (match(line, /[0-9]+\. [A-Za-z0-9_]+/)) {
      test = substr(line, RSTART, RLENGTH); sub(/\. /, ".", test)
      line = substr(line, RSTART + RLENGTH)
    }
    if (failed) print suite "/" test
  }
' "$work/litmus.out" | sort -u >"$work/failed"
grep -o '^[^#]*' "$expected_file" | tr -d ' \t' | grep -v '^$' | sort -u >"$work/expected"

status=0
for suite in $suites; do
  if ! grep -q "^<- summary for \`$suite':" "$work/litmus.out"; then
    echo "litmus did not finish the $suite suite" >&2
    status=1
  fi
done

unexpected="$(comm -23 "$work/failed" "$work/expected")"
fixed="$(comm -13 "$work/failed" "$work/expected")"
if [[ -n "$unexpected" ]]; then
  echo "Unexpected failures:" >&2
  sed 's/^/  /' <<<"$unexpected" >&2
  status=1
fi
if [[ -n "$fixed" ]]; then
  echo "Expected to fail but did not (passed, or skipped after an earlier failure; take a fixed one off $expected_file):" >&2
  sed 's/^/  /' <<<"$fixed" >&2
  status=1
fi
if [[ $status -eq 0 ]]; then
  echo "litmus: every failure is an expected one ($(wc -l <"$work/expected") listed)" >&2
fi
exit $status
