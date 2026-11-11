// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback } from "react";
import { errorText, isNotFound } from "../api/error-text";
import { useVaults } from "../api/notes";
import { vaultOf } from "../api/paths";
import { useDiscreet } from "./state";

/** A note of a discreet vault that isn't there: said without its path. */
export const NO_SUCH_NOTE = "There is no such note (if it was removed, it is in the trash).";

/**
 * Whether the user's vault list doesn't name `path`'s vault; a list not in yet, still loading or failed names none. It
 * only reads the list the app brought in, asking for nothing. After a reload the tab no longer knows a vault it hid, so
 * a note of it (a bookmark) is told apart by this alone.
 */
export function useUnnamedVault(path: string | null): boolean {
  const vaults = useVaults({ passive: true });
  return path !== null && (!vaults.data || !vaults.data.some((v) => v.name === vaultOf(path)));
}

/**
 * The sentence for a failed request about `path`. The note API's 404 names the path (or the vault), which mustn't be
 * on screen for a discreet vault, shown or hidden again in this tab (spec §3), nor for a vault the list doesn't name:
 * for such a vault it is said without it.
 */
export function useErrorTextFor(path: string): (error: unknown) => string {
  const discreet = useDiscreet((s) => s.names.has(vaultOf(path)) || s.gone.has(vaultOf(path)));
  const unnamed = useUnnamedVault(path);
  const hide = discreet || unnamed;
  return useCallback((error: unknown) => (hide && isNotFound(error) ? NO_SUCH_NOTE : errorText(error)), [hide]);
}
