#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# `pnpm e2e`, `pnpm e2e:0.5.0` and `pnpm e2e:0.6.0`: the stack up, Playwright with the given arguments, the stack
# down again. Two phases, so a failure in the parallel tests doesn't skip the serial ones: first setup, mcp, desktop
# and phone, then serial and serial-phone on the same setup. Exits with Playwright's worse exit code.
set -uo pipefail
STACK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# The stack and its volumes go, and with them the run's random passwords, PIN and sessions in e2e/.auth/, and the
# failures' screenshots and page snapshots in e2e/.results/, which may show a reader link.
trap '"$STACK_DIR/down.sh"; rm -rf "$STACK_DIR/../.auth" "$STACK_DIR/../.results"' EXIT
"$STACK_DIR/up.sh" || exit 1
cd "$STACK_DIR/../.."
pnpm exec playwright test --project=setup --project=mcp --project=desktop --project=phone --pass-with-no-tests "$@"
parallel=$?
[ -s e2e/.auth/secrets.json ] || exit "$parallel"
pnpm exec playwright test --no-deps --workers=1 --project=serial --project=serial-phone --pass-with-no-tests "$@"
serial=$?
exit $(( parallel > serial ? parallel : serial ))
