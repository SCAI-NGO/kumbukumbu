// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMemo, useState } from "react";
import { errorText } from "../api/error-text";
import { useRestoreNote } from "../api/history";
import { useHistory, useNote, useNoteVersion, type HistoryEntry } from "../api/notes";
import { titleOf } from "../api/paths";
import { FormError, secondaryButtonClass } from "../components/form";
import { Sheet, sizeText, timeText } from "../components/Sheet";
import { ago } from "./AgentBanner";
import { lineDiff } from "./lineDiff";
import MarkdownView from "./MarkdownView";
import { splitFrontmatter } from "./properties";

const noop = () => {};

/** The note's versions, newest first (writer, when, size); a tap shows one, and an earlier one can be restored. */
export default function HistorySheet({ path, onClose }: { path: string; onClose(): void }) {
  const history = useHistory(path, true);
  const [chosen, setChosen] = useState<HistoryEntry | null>(null);

  // One dialog for both steps, so the focus stays in it on the way to a version and back.
  if (chosen) {
    return (
      <Sheet title={chosen.current ? "Current version" : timeText(chosen.writtenAt)} onClose={onClose} onBack={() => setChosen(null)} backLabel="All versions">
        <VersionView path={path} entry={chosen} onBack={() => setChosen(null)} onRestored={onClose} />
      </Sheet>
    );
  }
  return (
    <Sheet title="History" onClose={onClose}>
      <p className="text-sm text-text-body">
        Up to 10 earlier versions of {titleOf(path)} are kept. Writes close together in time by one writer count as one version. Restoring one makes it
        the current version again; the version it replaces stays here.
      </p>
      {history.isPending && <p className="text-sm text-muted">Loading the versions …</p>}
      {history.isError && <FormError>{errorText(history.error)}</FormError>}
      {history.data && (
        <ul aria-label="Versions" className="flex flex-col gap-1.5">
          {history.data.map((v, i) => (
            <li key={`${i}-${v.version}`}>
              <button
                type="button"
                onClick={() => setChosen(v)}
                className="flex min-h-12 w-full flex-col items-start gap-0.5 rounded-[12px] border border-line bg-surface-2 px-3.5 py-2 text-left focus-visible:outline-2 focus-visible:outline-accent"
              >
                <span className="flex w-full items-baseline gap-2">
                  <span className="min-w-0 flex-1 truncate font-medium text-text">{v.current ? "Current version" : timeText(v.writtenAt)}</span>
                  <span className="flex-none text-[12px] text-dim">{ago(v.writtenAt)}</span>
                </span>
                <span className="flex w-full gap-2 text-sm text-muted">
                  <span className="min-w-0 flex-1 truncate">by {v.writer}</span>
                  <span className="flex-none font-mono text-[12px]">{sizeText(Number(v.size))}</span>
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </Sheet>
  );
}

const lineClass = {
  same: "text-muted",
  removed: "bg-surface-2 text-text before:content-['−_'] before:text-dim",
  added: "bg-surface text-text before:content-['+_'] before:text-accent-text",
};

/** One version: rendered, or as its line diff to the current one; restoring it is a new write. */
function VersionView({ path, entry, onBack, onRestored }: { path: string; entry: HistoryEntry; onBack(): void; onRestored(): void }) {
  const version = useNoteVersion(path, entry.version);
  const current = useNote(path);
  const restore = useRestoreNote();
  const [tab, setTab] = useState<"rendered" | "changes">("rendered");
  const body = useMemo(() => splitFrontmatter(version.data?.content ?? "").body, [version.data?.content]);
  const diff = useMemo(
    () => (version.data && current.data && tab === "changes" ? lineDiff(version.data.content, current.data.content) : null),
    [version.data, current.data, tab],
  );
  const same = !!version.data && !!current.data && version.data.content === current.data.content;

  const tabClass = (on: boolean) =>
    `min-h-11 flex-1 rounded-[10px] px-3 text-sm font-medium focus-visible:outline-2 focus-visible:outline-accent ${on ? "bg-accent text-on-accent" : "text-text-body"}`;

  return (
    <>
      <p className="text-sm text-muted">
        By {entry.writer}, {ago(entry.writtenAt)} · {sizeText(Number(entry.size))}
      </p>
      <div role="group" aria-label="Show" className="flex gap-1 rounded-[12px] border border-line p-1">
        <button type="button" aria-pressed={tab === "rendered"} onClick={() => setTab("rendered")} className={tabClass(tab === "rendered")}>
          This version
        </button>
        <button type="button" aria-pressed={tab === "changes"} onClick={() => setTab("changes")} className={tabClass(tab === "changes")}>
          Changes since
        </button>
      </div>
      {version.isError && <FormError>{errorText(version.error)}</FormError>}
      {tab === "rendered" && version.data && (
        <article aria-label="This version" className="rounded-[12px] border border-line bg-bg px-4 py-3">
          <MarkdownView source={body} links={undefined} tasksDisabled onToggleTask={noop} onCreateMissing={noop} />
        </article>
      )}
      {tab === "changes" &&
        (same ? (
          <p className="text-sm text-text-body">The current version has the same text.</p>
        ) : (
          diff && (
            <>
              <p className="text-sm text-text-body">
                <span className="font-mono text-dim">−</span> only in this version · <span className="font-mono text-accent-text">+</span> only in the current one
              </p>
              <ol aria-label="Changes since" className="overflow-auto rounded-[10px] border border-line bg-bg py-2 font-mono text-[13px] leading-[1.6]">
                {diff.map((line, i) => (
                  <li key={i} data-kind={line.kind} className={`min-h-[1.6em] px-3 whitespace-pre-wrap ${lineClass[line.kind]}`}>
                    <span>{line.text}</span>
                  </li>
                ))}
              </ol>
            </>
          )
        ))}
      {restore.isError && <FormError>{errorText(restore.error)}</FormError>}
      {!entry.current && (
        <div className="sticky bottom-0 flex flex-col gap-2 bg-surface pt-1">
          <p className="text-sm text-text-body">Restoring makes this text the current version. Nothing is lost: the version it replaces stays in the history.</p>
          <div className="flex gap-2.5">
            <button type="button" onClick={onBack} className={`${secondaryButtonClass} flex-1`}>
              Back
            </button>
            <button
              type="button"
              disabled={restore.isPending || same}
              onClick={() => restore.mutate({ path, version: entry.version }, { onSuccess: onRestored })}
              className="min-h-11 flex-1 rounded-[10px] bg-accent px-4 font-medium text-on-accent disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
            >
              Restore this version
            </button>
          </div>
        </div>
      )}
    </>
  );
}
