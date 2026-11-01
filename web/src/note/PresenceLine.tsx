// SPDX-License-Identifier: AGPL-3.0-or-later
import { useEffect } from "react";
import { inVault, vaultOf } from "../api/paths";
import { presenceKey, useSyncStatus } from "../state/sync";

/** How often an open editor says again that it is one: the server forgets an editor after 60 s without. */
export const EDITING_REFRESH = 30_000;

/**
 * "Bob is editing this note", "Bob, Carol and 2 other users are editing this note". The names are the server's, as
 * this user is shown them (collab design §8.2, §9.2): the owner sees her labels for the members, a member the owner's
 * name and the others only counted ("another user", "2 other users"); "You" is her own other tab.
 */
export function presenceText(editors: string[]): string | null {
  if (editors.length === 0) return null;
  const names = editors.length === 1 ? editors[0] : `${editors.slice(0, -1).join(", ")} and ${editors.at(-1)}`;
  const many = editors.length > 1 || editors[0] === "You" || /^\d+ other users$/.test(editors[0]);
  return `${names.charAt(0).toUpperCase()}${names.slice(1)} ${many ? "are" : "is"} editing this note${editors.length === 1 && editors[0] === "You" ? " in another tab" : ""}`;
}

/** Above the note in Write mode: who else is editing it now, if anyone. */
export default function PresenceLine({ path }: { path: string }) {
  const editors = useSyncStatus((s) => s.editors[presenceKey(vaultOf(path), inVault(path))]);
  const text = presenceText(editors ?? []);
  if (!text) return null;
  return (
    <p role="status" aria-label="Editing now" className="flex min-h-9 flex-none items-center gap-2 border-b border-line bg-surface px-4 text-[13px] text-muted">
      <span aria-hidden className="size-2 flex-none rounded-full bg-accent" />
      <span className="min-w-0 truncate">{text}</span>
    </p>
  );
}

/**
 * While `active` (the note open in Write mode), the socket tells the server so, every 30 s again, and once more after
 * a reconnect, whose server knows nothing of it; and that it stopped when Write mode ends or the note closes.
 */
export function useEditingPresence(path: string, active: boolean) {
  const connected = useSyncStatus((s) => s.connected);
  useEffect(() => {
    if (!active || !connected) return;
    const frame = (on: boolean) => useSyncStatus.getState().send({ type: "editing", vault: vaultOf(path), path: inVault(path), on });
    frame(true);
    const timer = setInterval(() => frame(true), EDITING_REFRESH);
    return () => {
      clearInterval(timer);
      frame(false);
    };
  }, [path, active, connected]);
}
