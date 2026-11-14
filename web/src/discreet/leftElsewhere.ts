// SPDX-License-Identifier: AGPL-3.0-or-later
import type { QueryClient } from "@tanstack/react-query";
import type { NavigateFunction } from "react-router-dom";
import { vaultOf } from "../api/paths";
import { useConsole } from "../console/store";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { vaultOfRoute } from "./neutralWindow";
import { useDiscreet } from "./state";
import { sweep } from "./sweep";

/** Whether an answer names one of `names`, as a vault (`"Tagebuch"`) or by a path in it (`/Tagebuch/a.md`). */
const named = (data: unknown, names: ReadonlySet<string>) => {
  const text = JSON.stringify(data) ?? "";
  return [...names].some((n) => text.includes(`/${n}/`) || text.includes(`"${n}"`));
};

/**
 * Vaults this tab showed that the list no longer has, as another session of the user marked them discreet (or they
 * were taken from her): a view of one gives way to the vault overview at once, the editor forgets them as a session's
 * end does (an unsaved draft stays, and opens again once they are shown), and their cached answers go, what was found
 * or changed in them too; the console forgets its scrollback and history, as hiding again does. Text unsaved in the
 * open note of one is kept as the draft only when there is no draft already: there is one draft, and the earlier one
 * stays, so that text is then lost. Kept or not, the note goes: the server takes no save of it now, and the editor's
 * guard would keep it on screen.
 */
export function leaveVaults(queryClient: QueryClient, navigate: NavigateFunction, pathname: string, names: ReadonlySet<string>) {
  const editor = useEditor.getState();
  const open = editor.currentPath !== null && names.has(vaultOf(editor.currentPath)) ? editor.currentPath : null;
  const username = useSession.getState().user?.username;
  const text = open ? editor.unsaved?.() : null;
  if (open && text && username && !editor.draft) editor.setDraft({ username, path: open, ...text });

  // As while hiding: a view of them that renders once more shows nothing of them, and the guard lets the note go. The
  // guard is asked as the navigation starts, so `hiding` is back before anything else runs; a hide under way keeps it.
  const hiding = useDiscreet.getState().hiding;
  useDiscreet.setState({ hiding: true, gone: new Set([...useDiscreet.getState().gone, ...names]) });
  try {
    const shown = vaultOfRoute(pathname);
    if ((shown !== null && names.has(shown)) || open) navigate("/", { replace: true });
    useEditor.getState().forgetShown(names);
  } finally {
    if (!hiding) useDiscreet.setState({ hiding: false });
  }

  // What was found, what changed, and Recent changes list notes of every vault under keys that name none.
  void queryClient.resetQueries({
    predicate: (q) => ["search", "changes", "recent"].includes(q.queryKey[0] as string) && q.state.data !== undefined && named(q.state.data, names),
  });
  sweep(queryClient, names);
  // A command needn't name the vault to have shown it (`ls` at the root).
  useConsole.getState().reset();
}
