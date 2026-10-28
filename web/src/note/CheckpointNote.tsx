// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMemo, useState } from "react";
import { errorText } from "../api/error-text";
import { useCheckpointNote } from "../api/history";
import { checkpointUrl, isAttachment, titleOf, viewUrl } from "../api/paths";
import { secondaryButtonClass } from "../components/form";
import { aiPrompt } from "./copyForAi";
import MarkdownView from "./MarkdownView";
import { Properties, startsWithH1 } from "./NoteView";
import { splitFrontmatter } from "./properties";

const noop = () => {};

/**
 * One note of a checkpoint, as it was. It is rendered by the same viewer as today's notes but cannot write: no
 * editor, no autosave, no ticking of tasks. Outline, find and backlinks are left out on purpose, as they would
 * read today's vault.
 */
export default function CheckpointNote({ vault, name, createdAt, path }: { vault: string; name: string; createdAt: string; path: string }) {
  const note = useCheckpointNote(vault, name, path);
  const parts = useMemo(() => splitFrontmatter(note.data?.content ?? ""), [note.data?.content]);
  const [copy, setCopy] = useState<"copied" | "failed" | null>(null);
  // A note it holds opens in it; an image or PDF in the lightbox, as the checkpoint held it.
  const checkpointLink = useMemo(() => (p: string) => (isAttachment(p) ? viewUrl(p, { checkpoint: name }) : checkpointUrl(vault, name, p)), [vault, name]);

  // A missing clipboard (plain http, no permission) counts as a refusal: never claim a copy that did not happen.
  const copyForAi = async () => {
    try {
      await navigator.clipboard.writeText(aiPrompt(vault, name, createdAt, path));
      setCopy("copied");
    } catch {
      setCopy("failed");
    }
  };

  return (
    <div className="min-h-0 flex-1 overflow-y-auto px-[22px] pt-[22px] pb-7">
      {!(note.data && !note.isError && startsWithH1(parts.body)) && <h1 className="text-[28px] leading-tight font-bold text-text">{titleOf(path)}</h1>}
      <div className="mt-3 flex flex-col items-start gap-1.5">
        <button type="button" onClick={copyForAi} className={secondaryButtonClass}>
          Copy for your AI
        </button>
        {copy === "copied" && (
          <p role="status" className="text-sm text-muted">
            Copied. Paste it into your AI's chat.
          </p>
        )}
        {copy === "failed" && (
          <p role="alert" className="text-sm text-text-body">
            Couldn't copy. Select the text and copy it by hand.
          </p>
        )}
      </div>
      {note.isPending && <p className="mt-4 text-sm text-muted">Loading …</p>}
      {note.isError && <p className="mt-4 text-text-body">{errorText(note.error)}</p>}
      {note.data && !note.isError && (
        <article className="mt-4">
          {parts.frontmatter !== null && <Properties yaml={parts.frontmatter} changed={null} still />}
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
