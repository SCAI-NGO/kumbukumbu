// SPDX-License-Identifier: AGPL-3.0-or-later
import { useCallback } from "react";
import { useNavigate } from "react-router-dom";
import { paneUrl } from "../api/paths";
import { useEditor, type Side } from "../state/editor";

/**
 * The split view's panes by the URL, which the shell's store follows (`?beside=`), written from the store's panes as a
 * pane closes: in the store first, a writing one once it has saved (`closePane`), and the URL follows only then. A note
 * opened in a pane is `useOpenNote`'s.
 */
export function usePaneUrl() {
  const navigate = useNavigate();
  const close = useCallback(
    async (side: Side) => {
      // With one pane there is nothing to close, and no entry to write.
      if (!useEditor.getState().panes.right) return;
      await useEditor.getState().closePane(side);
      const { left, right } = useEditor.getState().panes;
      if (!right && left.path) navigate(paneUrl(left.path, null));
    },
    [navigate],
  );
  return { close };
}
