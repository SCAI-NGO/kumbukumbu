#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# `pnpm e2e:0.7.0`: the 0.7.0 tags in two runs, each on a fresh stack (run.sh): first with search by meaning off,
# everything but @search; then with it on (E2E_SEARCH=1, compose.search.yaml), the @search tests but @tensorx, which
# needs the proxy's TensorX secret (`pnpm e2e:0.7.0:tensorx`). The second runs whatever the first gave; exits with the
# worse code.
set -uo pipefail
STACK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TAGS="@smoke|@offline|@devices|@sw|@graph|@similar|@synthesis|@overview"
"$STACK_DIR/run.sh" --grep "$TAGS" --grep-invert "@search" "$@"
off=$?
E2E_SEARCH=1 "$STACK_DIR/run.sh" --grep "@search" --grep-invert "@tensorx" "$@"
on=$?
exit $(( off > on ? off : on ))
