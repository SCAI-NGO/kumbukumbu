// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import { covered, useSetUnpublishable } from "../api/publications";
import { primaryButtonClass, secondaryButtonClass } from "../components/form";
import { useDialog } from "../hooks/useDialog";

/**
 * The menus' mark entries: a mark that covers a published note withdraws its link (publish spec §3.6), so that one is
 * asked first; any other mark, and lifting one, is set at once. `published`: the vault's published notes.
 */
export function useMarking(published: readonly string[] | undefined) {
  const setMark = useSetUnpublishable();
  const [asking, setAsking] = useState<string | null>(null);
  const mark = (path: string, marked: boolean) => {
    if (marked && published?.some((p) => covered([path], p))) setAsking(path);
    else setMark.mutate({ path, marked });
  };
  const question = asking && (
    <ConfirmMark
      busy={setMark.isPending}
      onKeep={() => setAsking(null)}
      onMark={() => setMark.mutate({ path: asking, marked: true }, { onSettled: () => setAsking(null) })}
    />
  );
  return { mark, question, error: setMark.isError ? setMark.error : null };
}

function ConfirmMark({ busy, onKeep, onMark }: { busy: boolean; onKeep(): void; onMark(): void }) {
  const dialog = useDialog<HTMLDivElement>(onKeep);
  const id = useId();
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-bg/80 p-4 sm:items-center">
      <div
        ref={dialog}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby={`${id}-title`}
        className="flex w-full max-w-sm flex-col gap-4 rounded-[14px] border border-line bg-surface-2 p-5"
      >
        <p id={`${id}-title`} className="text-text-body">
          Marking it unpublishable withdraws its published link: whoever has it can't read the note any more.
        </p>
        <div className="flex gap-3">
          <button type="button" data-autofocus onClick={onKeep} className={`${secondaryButtonClass} flex-1`}>
            Keep
          </button>
          <button type="button" disabled={busy} onClick={onMark} className={`${primaryButtonClass} flex-1`}>
            Mark unpublishable
          </button>
        </div>
      </div>
    </div>
  );
}
