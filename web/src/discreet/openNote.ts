// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect, useRef } from "react";
import { api } from "../api/client";
import { inVault, vaultOf } from "../api/paths";
import { useEditor } from "../state/editor";
import { useDiscreet } from "./state";

/**
 * While the discreet vaults are shown, the server knows which discreet note this tab has open: when the time runs out,
 * it takes one late save of that note (spec §3.8). Told when it changes; once an ordinary note (or none) is open after
 * a discreet one, that none is. Nothing while they are hidden or being hidden: a hide clears it on the server.
 */
export function useTellOpenNote() {
  const revealed = useDiscreet((s) => s.revealed);
  const names = useDiscreet((s) => s.names);
  const currentPath = useEditor((s) => s.currentPath);
  const open = revealed && currentPath && names.has(vaultOf(currentPath)) ? currentPath : null;
  const told = useRef<string | null>(null);

  useEffect(() => {
    if (!revealed) {
      told.current = null;
      return;
    }
    if (open === told.current || useDiscreet.getState().hiding) return;
    told.current = open;
    const body = open ? { vault: vaultOf(open), path: inVault(open) } : { vault: null };
    void api.PUT("/api/account/discreet/open-note", { body }).catch(() => {});
  }, [revealed, open]);
}
