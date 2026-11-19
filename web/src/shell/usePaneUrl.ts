// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback } from "react";
import { useNavigate } from "react-router-dom";
import { paneUrl } from "../api/paths";
import { useEditor, type Side } from "../state/editor";

/**
 * The split view's panes by the URL, which the shell's store follows (`?beside=`), written from the store's panes:
 * a note opened in the right pane is a new history entry, as one opened on the left is; a pane closes in the store
 * first, a writing one once it has saved (`closePane`), and the URL follows only then.
 */
export function usePaneUrl() {
  const navigate = useNavigate();
  const openBeside = useCallback(
    (path: string) => {
      const left = useEditor.getState().panes.left.path;
      if (left) navigate(paneUrl(left, path));
    },
    [navigate],
  );
  const close = useCallback(
    async (side: Side) => {
      await useEditor.getState().closePane(side);
      const { left, right } = useEditor.getState().panes;
      if (!right && left.path) navigate(paneUrl(left.path, null));
    },
    [navigate],
  );
  return { openBeside, close };
}
