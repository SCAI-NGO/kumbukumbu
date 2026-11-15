# SPDX-License-Identifier: AGPL-3.0-or-later
# Sourced by up.sh and down.sh: always the project kumbu-e2e with explicit files, never a developer's own stack.
set -euo pipefail
unset COMPOSE_FILE COMPOSE_PROJECT_NAME
STACK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$STACK_DIR/../../.." && pwd)"
# An empty folder for the seed mount: with no dev token the server seeds nothing anyway.
E2E_VAULTS="${E2E_VAULTS:-${TMPDIR:-/tmp}/kumbu-e2e-vaults}"
# The temporary directory may be shared: a folder of the stack's is used only when this user made it, and one
# someone else made stops the run (remove it, or set TMPDIR to a folder of your own).
own_or_absent() {
  if [ -e "$1" ] || [ -L "$1" ]; then
    if [ -L "$1" ] || [ ! -O "$1" ]; then echo "$1 isn't this user's: remove it, or set TMPDIR to a folder of your own." >&2; exit 1; fi
  fi
}
FILES=(-f "$REPO_DIR/compose.yaml" -f "$REPO_DIR/compose.build.yaml" -f "$STACK_DIR/compose.e2e.yaml")
if [ -n "${E2E_PROXY_CA:-}" ]; then FILES+=(-f "$STACK_DIR/compose.proxy-ca.yaml"); fi
export KUMBU_VAULTS="$E2E_VAULTS" KUMBU_PORT=8080 KUMBU_PUBLIC_URL=http://localhost:8080 KUMBU_DEV_TOKEN=
compose() { docker compose -p kumbu-e2e --project-directory "$REPO_DIR" "${FILES[@]}" "$@"; }
