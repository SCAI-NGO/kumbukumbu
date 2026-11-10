// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback } from "react";
import { HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { vaultOf } from "../api/paths";
import { useDiscreet } from "./state";

/** A note of a discreet vault that isn't there: said without its path. */
export const NO_SUCH_NOTE = "There is no such note (if it was removed, it is in the trash).";

/**
 * The sentence for a failed request about `path`. The note API's 404 names the path (or the vault), which mustn't be
 * on screen for a discreet vault, shown or hidden again in this tab (spec §3): for such a vault it is said without it.
 */
export function useErrorTextFor(path: string): (error: unknown) => string {
  const discreet = useDiscreet((s) => s.names.has(vaultOf(path)) || s.gone.has(vaultOf(path)));
  return useCallback((error: unknown) => (discreet && error instanceof HttpError && error.status === 404 ? NO_SUCH_NOTE : errorText(error)), [discreet]);
}
