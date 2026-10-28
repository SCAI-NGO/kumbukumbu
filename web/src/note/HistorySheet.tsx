// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import Thumbnail from "../attachments/Thumbnail";
import { HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { useRestoreNote } from "../api/history";
import { useHistory, useNote, useNoteVersion, type HistoryEntry } from "../api/notes";
import { isAttachment, titleOf, vaultOf, viewUrl, type LightboxState } from "../api/paths";
import { FormError, secondaryButtonClass } from "../components/form";
import { Sheet, sizeText, timeText } from "../components/Sheet";
import { CompareFiles } from "../shell/CompareVersions";
import { ago } from "./AgentBanner";
import { lineDiff } from "./lineDiff";
import MarkdownView from "./MarkdownView";
import { splitFrontmatter } from "./properties";

const noop = () => {};

type Props = { path: string; onClose(): void; onRestored?(): void };

/**
 * The versions of a note or an attachment, newest first (writer, when, size); a tap shows one, and an earlier one
 * can be restored. `onRestored`: what follows a restore, by default closing.
 */
export default function HistorySheet(props: Props) {
  return isAttachment(props.path) ? <FileHistory {...props} /> : <NoteHistory {...props} />;
}

const rowText = (v: HistoryEntry) => (
  <>
    <span className="flex w-full items-baseline gap-2">
      <span className="min-w-0 flex-1 truncate font-medium text-text">{v.current ? "Current version" : timeText(v.writtenAt)}</span>
      <span className="flex-none text-[12px] text-dim">{ago(v.writtenAt)}</span>
    </span>
    <span className="flex w-full gap-2 text-sm text-muted">
      <span className="min-w-0 flex-1 truncate">
        by {v.writer}
        {v.writerKind === "device" && " · Obsidian device"}
      </span>
      <span className="flex-none font-mono text-[12px]">{sizeText(Number(v.size))}</span>
    </span>
  </>
);

function NoteHistory({ path, onClose, onRestored = onClose }: Props) {
  const history = useHistory(path, true);
  const [chosen, setChosen] = useState<HistoryEntry | null>(null);

  // One dialog for both steps, so the focus stays in it on the way to a version and back.
  if (chosen) {
    return (
      <Sheet title={chosen.current ? "Current version" : timeText(chosen.writtenAt)} onClose={onClose} onBack={() => setChosen(null)} backLabel="All versions">
        <VersionView path={path} entry={chosen} expected={history.data?.[0]?.version} onBack={() => setChosen(null)} onRestored={onRestored} />
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
                {rowText(v)}
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
/** `expected`: the current version as the list says; a restore never goes on top of a newer one it hasn't shown. */
function VersionView({ path, entry, expected, onBack, onRestored }: { path: string; entry: HistoryEntry; expected: string | undefined; onBack(): void; onRestored(): void }) {
  const version = useNoteVersion(path, entry.version);
  // The text shown, as it is: a read now would replace it under the reader.
  const current = useNote(path, { refetchOnMount: false });
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
        By {entry.writer}
        {entry.writerKind === "device" && " (Obsidian device)"}, {ago(entry.writtenAt)} · {sizeText(Number(entry.size))}
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
      <RestoreBar path={path} entry={entry} expected={expected} same={same} what="this text" onBack={onBack} onRestored={onRestored} />
    </>
  );
}

/** Below a version: restoring it, on top of `expected` only; `same`: it reads as the current one, nothing to restore. */
function RestoreBar({
  path,
  entry,
  expected,
  same,
  what,
  onBack,
  onRestored,
}: {
  path: string;
  entry: HistoryEntry;
  expected: string | undefined;
  same: boolean;
  what: string;
  onBack(): void;
  onRestored(): void;
}) {
  const queryClient = useQueryClient();
  const restore = useRestoreNote();
  return (
    <>
      {restore.isError && (
        <FormError>
          {restore.error instanceof HttpError && restore.error.status === 409
            ? "A newer version arrived meanwhile, so nothing was restored. The list of versions shows it now; look again before restoring."
            : errorText(restore.error)}
        </FormError>
      )}
      {!entry.current && (
        <div className="sticky bottom-0 flex flex-col gap-2 bg-surface pt-1">
          <p className="text-sm text-text-body">Restoring makes {what} the current version. Nothing is lost: the version it replaces stays in the history.</p>
          <div className="flex gap-2.5">
            <button type="button" onClick={onBack} className={`${secondaryButtonClass} flex-1`}>
              Back
            </button>
            <button
              type="button"
              disabled={restore.isPending || same}
              onClick={() =>
                restore.mutate(
                  { path, version: entry.version, expected },
                  {
                    onSuccess: onRestored,
                    onError: (err) => {
                      if (err instanceof HttpError && err.status === 409) void queryClient.invalidateQueries({ queryKey: ["history", path] });
                    },
                  },
                )
              }
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

/**
 * An attachment's versions as pictures (a PDF's first page) with date and writer. A tap on the picture shows that
 * version full screen in the lightbox, read only; a tap beside it shows it next to the current one, to restore.
 */
function FileHistory({ path, onClose, onRestored = onClose }: Props) {
  const history = useHistory(path, true);
  const [chosen, setChosen] = useState<HistoryEntry | null>(null);
  const navigate = useNavigate();
  const location = useLocation();

  const show = (v: HistoryEntry) => {
    onClose();
    const to = viewUrl(path, v.current ? {} : { version: v.version });
    // The current version from the lightbox showing it: closing the history is all there is to do.
    if (`${location.pathname}${location.search}` !== to) navigate(to, { state: { paths: [path] } satisfies LightboxState });
  };

  if (chosen) {
    return (
      <Sheet title={chosen.current ? "Current version" : timeText(chosen.writtenAt)} onClose={onClose} onBack={() => setChosen(null)} backLabel="All versions">
        <p className="text-sm text-muted">
          By {chosen.writer}
          {chosen.writerKind === "device" && " (Obsidian device)"}, {ago(chosen.writtenAt)} · {sizeText(Number(chosen.size))}
        </p>
        <CompareFiles
          path={path}
          sides={chosen.current ? [{ label: "This version", version: null }] : [{ label: "This version", version: chosen.version }, { label: "Now", version: null }]}
        />
        <RestoreBar
          path={path}
          entry={chosen}
          expected={history.data?.[0]?.version}
          same={chosen.version === history.data?.[0]?.version}
          what="this version"
          onBack={() => setChosen(null)}
          onRestored={onRestored}
        />
      </Sheet>
    );
  }
  return (
    <Sheet title="History" onClose={onClose}>
      <p className="text-sm text-text-body">
        Up to 10 earlier versions of {titleOf(path)} are kept. Tap a picture to see that version full screen. Restoring one makes it the current version
        again; the version it replaces stays here.
      </p>
      {history.isPending && <p className="text-sm text-muted">Loading the versions …</p>}
      {history.isError && <FormError>{errorText(history.error)}</FormError>}
      {history.data && (
        <ul aria-label="Versions" className="flex flex-col gap-1.5">
          {history.data.map((v, i) => (
            <li key={`${i}-${v.version}`} className="flex items-stretch gap-1 rounded-[12px] border border-line bg-surface-2 p-1.5">
              <button
                type="button"
                aria-label={v.current ? "Show the current version" : `Show the version of ${timeText(v.writtenAt)}`}
                onClick={() => show(v)}
                className="relative size-16 flex-none overflow-hidden rounded-[8px] bg-surface focus-visible:outline-2 focus-visible:outline-accent"
              >
                <Thumbnail vault={vaultOf(path)} path={path} at={v.current ? {} : { version: v.version }} />
              </button>
              <button
                type="button"
                onClick={() => setChosen(v)}
                className="flex min-h-12 min-w-0 flex-1 flex-col items-start justify-center gap-0.5 rounded-[8px] px-2 text-left focus-visible:outline-2 focus-visible:outline-accent"
              >
                {rowText(v)}
              </button>
            </li>
          ))}
        </ul>
      )}
    </Sheet>
  );
}
