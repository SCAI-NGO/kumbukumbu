// SPDX-License-Identifier: AGPL-3.0-or-later
import type { Query, QueryClient } from "@tanstack/react-query";
import type { NavigateFunction } from "react-router-dom";
import { noteUrl, vaultOf } from "../api/paths";
import { badgeQuery } from "../calendar/api";
import { clearAppBadge } from "../calendar/useBadge";
import { useConsole } from "../console/store";
import { editedPath, useEditor } from "../state/editor";
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
  // The editor's note, in either pane of the split view; a note of theirs only in the right pane leaves that pane.
  const edited = editedPath(editor);
  const open = edited !== null && names.has(vaultOf(edited)) ? edited : null;
  const { left: leftPane, right } = editor.panes;
  const note = pathname.startsWith("/notes/");
  const besideOnly = !!right && names.has(vaultOf(right.path)) && leftPane.path !== null && !names.has(vaultOf(leftPane.path)) && note;
  // The other way round, the right pane becomes the only one (`forgetShown`), and the URL goes to it, as hiding again does.
  const besideStays = !!right && leftPane.path !== null && names.has(vaultOf(leftPane.path)) && !names.has(vaultOf(right.path)) && note;
  const username = useSession.getState().user?.username;
  const text = open ? editor.unsaved?.() : null;
  if (open && text && username && !editor.draft) editor.setDraft({ username, path: open, ...text });

  // As while hiding: a view of them that renders once more shows nothing of them, and the guard lets the note go. The
  // guard is asked as the navigation starts, so `hiding` is back before anything else runs; a hide under way keeps it.
  const hiding = useDiscreet.getState().hiding;
  useDiscreet.setState({ hiding: true, gone: new Set([...useDiscreet.getState().gone, ...names]) });
  try {
    const shown = vaultOfRoute(pathname);
    if (besideOnly) navigate(noteUrl(leftPane.path!), { replace: true });
    else if (besideStays) navigate(noteUrl(right.path), { replace: true });
    else if ((shown !== null && names.has(shown)) || open) navigate("/", { replace: true });
    useEditor.getState().forgetShown(names);
  } finally {
    if (!hiding) useDiscreet.setState({ hiding: false });
  }

  // What was found, what changed, Recent changes and What's on list notes of every vault under keys that name none. As
  // hiding again does, the app's icon is cleared when the count counted them, until the new count is in.
  const left = (q: Query) =>
    ["search", "changes", "recent", "whats-on"].includes(q.queryKey[0] as string) && q.state.data !== undefined && named(q.state.data, names);
  if (queryClient.getQueryCache().findAll({ queryKey: badgeQuery.queryKey, exact: true, predicate: left }).length > 0) clearAppBadge();
  void queryClient.resetQueries({ predicate: left });
  sweep(queryClient, names);
  // A command needn't name the vault to have shown it (`ls` at the root).
  useConsole.getState().reset();
}
