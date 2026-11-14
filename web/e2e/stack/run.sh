#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# `pnpm e2e` and `pnpm e2e:0.5.0`: the stack up, Playwright with the given arguments, the stack down again,
# and Playwright's exit code.
set -uo pipefail
STACK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
trap '"$STACK_DIR/down.sh"' EXIT
"$STACK_DIR/up.sh" || exit 1
cd "$STACK_DIR/../.."
pnpm exec playwright test "$@"
