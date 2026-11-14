#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Removes the suite's stack with its volumes: the next run starts from nothing.
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
if [ "${E2E_STACK:-compose}" = dotnet ]; then exec "$STACK_DIR/dotnet.sh" down; fi
compose down -v --remove-orphans
own_or_absent "$E2E_VAULTS"
rm -rf "$E2E_VAULTS"
