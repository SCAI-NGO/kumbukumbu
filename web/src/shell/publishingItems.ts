// SPDX-License-Identifier: AGPL-3.0-or-later
import { Eye, EyeOff, Share2 } from "lucide-react";
import { covered } from "../api/publications";
import type { MenuItem } from "../components/Menu";

/**
 * The owner's publishing entries of a note's, file's or folder's menu (publish spec §3.6, §3.11), for her own vault
 * only. "Publish…" only for a note not published (a published one has its bar) while the server publishes, and
 * disabled where a mark covers it; the mark's entry whatever the switch says, as marks only reduce what can go out.
 * Unknown marks (`undefined`, still asking or failed) offer neither: one could cover the note, and which mark entry is
 * right isn't known.
 */
export function publishingItems({
  path,
  note,
  marks,
  mayPublish,
  onPublish,
  onMark,
}: {
  path: string;
  note: boolean;
  marks: readonly string[] | undefined;
  /** The server publishes and the note isn't published yet. */
  mayPublish: boolean;
  onPublish(): void;
  onMark(marked: boolean): void;
}): MenuItem[] {
  const blocked = !!marks && covered(marks, path);
  const marked = !!marks?.includes(path);
  return [
    ...(note && mayPublish && marks
      ? [blocked ? { label: "Publish… (unpublishable)", icon: Share2, onSelect: () => {}, disabled: true } : { label: "Publish…", icon: Share2, onSelect: onPublish }]
      : []),
    ...(!marks ? [] : marked ? [{ label: "Allow publishing", icon: Eye, onSelect: () => onMark(false) }] : [{ label: "Mark unpublishable", icon: EyeOff, onSelect: () => onMark(true) }]),
  ];
}
