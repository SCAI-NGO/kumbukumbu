#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Prints the server's log of the suite's stack.
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
if [ "${E2E_STACK:-compose}" = dotnet ]; then exec cat "${TMPDIR:-/tmp}/kumbu-e2e-dotnet/server.log"; fi
compose logs --no-color kumbu
