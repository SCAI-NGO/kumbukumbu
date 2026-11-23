// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect } from "react";
import { useVaults } from "../api/notes";
import { inVault } from "../api/paths";
import { openedOnline } from "./engines";

/** An attachment opened in the viewer (`path`: `/<vault>/<path>`) is kept on this device within the budget (design §6). */
export function useOpenedOnline(vault: string, path: string, current: boolean) {
  const id = useVaults({ passive: true }).data?.find((v) => v.name === vault)?.id;
  useEffect(() => {
    if (current && id !== undefined) openedOnline(id, inVault(path));
  }, [current, id, path]);
}
