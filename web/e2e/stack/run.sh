#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# `pnpm e2e` and `pnpm e2e:0.5.0`: the stack up, Playwright with the given arguments, the stack down again.
# Two phases, so a failure in the parallel tests doesn't skip the serial ones: first setup, desktop and phone,
# then serial and serial-phone on the same setup. Exits with Playwright's worse exit code.
set -uo pipefail
STACK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
trap '"$STACK_DIR/down.sh"' EXIT
"$STACK_DIR/up.sh" || exit 1
cd "$STACK_DIR/../.."
pnpm exec playwright test --project=setup --project=desktop --project=phone --pass-with-no-tests "$@"
parallel=$?
[ -s e2e/.auth/secrets.json ] || exit "$parallel"
pnpm exec playwright test --no-deps --project=serial --project=serial-phone --pass-with-no-tests "$@"
serial=$?
exit $(( parallel > serial ? parallel : serial ))
