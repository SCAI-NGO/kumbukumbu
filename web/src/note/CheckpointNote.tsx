// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMemo } from "react";
import { errorText } from "../api/error-text";
import { useCheckpointNote } from "../api/history";
import { checkpointUrl, titleOf } from "../api/paths";
import MarkdownView from "./MarkdownView";
import { Properties, startsWithH1 } from "./NoteView";
import { splitFrontmatter } from "./properties";

const noop = () => {};

// `createdAt` is for the copy-for-your-AI action of a later task.
/**
 * One note of a checkpoint, as it was. It is rendered by the same viewer as today's notes but cannot write: no
 * editor, no autosave, no ticking of tasks. Outline, find and backlinks are left out on purpose, as they would
 * read today's vault.
 */
export default function CheckpointNote({ vault, name, path }: { vault: string; name: string; createdAt: string; path: string }) {
  const note = useCheckpointNote(vault, name, path);
  const parts = useMemo(() => splitFrontmatter(note.data?.content ?? ""), [note.data?.content]);
  const checkpointLink = useMemo(() => (p: string) => checkpointUrl(vault, name, p), [vault, name]);

  return (
    <div className="min-h-0 flex-1 overflow-y-auto px-[22px] pt-[22px] pb-7">
      {!(note.data && !note.isError && startsWithH1(parts.body)) && <h1 className="text-[28px] leading-tight font-bold text-text">{titleOf(path)}</h1>}
      {note.isPending && <p className="mt-4 text-sm text-muted">Loading …</p>}
      {note.isError && <p className="mt-4 text-text-body">{errorText(note.error)}</p>}
      {note.data && !note.isError && (
        <article className="mt-4">
          {parts.frontmatter !== null && <Properties yaml={parts.frontmatter} changed={null} />}
          <MarkdownView
            source={parts.body}
            links={note.data.links}
            tasksDisabled
            onToggleTask={noop}
            onCreateMissing={noop}
            path={path}
            checkpointLink={checkpointLink}
          />
        </article>
      )}
    </div>
  );
}
