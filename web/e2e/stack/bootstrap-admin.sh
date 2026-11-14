#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Prints the first admin's /join link (and nothing else) from the suite's stack.
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
if [ "${E2E_STACK:-compose}" = dotnet ]; then exec "$STACK_DIR/dotnet.sh" bootstrap-admin; fi
compose exec -T kumbu kumbu bootstrap-admin
