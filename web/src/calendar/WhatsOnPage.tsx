// SPDX-License-Identifier: AGPL-3.0-or-later
import { ArrowLeft } from "lucide-react";
import { useState } from "react";
import { Link } from "react-router-dom";
import { errorText } from "../api/error-text";
import { useMayChange } from "../api/notes";
import { calendarUrl } from "../api/paths";
import { FormError } from "../components/form";
import { useWhatsOn, type EntryView, type WhatsOnView } from "./api";
import { useClosing } from "./ClosedToast";
import EntryItem, { type EntryActions } from "./EntryItem";
import LaterMenu, { useMoved, type Moved } from "./LaterMenu";
import TagFilter, { useTag } from "./TagFilter";

/**
 * `/whats-on`: what is on over every vault the session shows (spec §7.6), overdue first, then ongoing, then upcoming;
 * each with its vault, and ✓ and Later on an open to-do. At the bottom, the dates that couldn't be read, if any. `?tag=`
 * narrows it to a tag.
 */
export default function WhatsOnPage() {
  const whatsOn = useWhatsOn(useTag());
  return (
    <div className="relative flex h-dvh flex-col bg-bg">
      <header className="flex flex-none items-center gap-1 border-b border-line px-2 py-1.5 lg:px-3">
        <Link to="/" aria-label="Back to the notes" className="flex size-12 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent">
          <ArrowLeft size={20} aria-hidden />
        </Link>
        <h1 className="min-w-0 flex-1 truncate px-2 text-[20px] font-bold text-text">What's on</h1>
        <TagFilter />
      </header>
      <main className="min-h-0 flex-1 overflow-y-auto px-3 py-3 lg:px-4">
        {whatsOn.isPending && <p className="text-sm text-muted">Loading …</p>}
        {whatsOn.isError && <p className="text-text">{errorText(whatsOn.error)}</p>}
        {whatsOn.data && <WhatsOn view={whatsOn.data} />}
      </main>
    </div>
  );
}

function WhatsOn({ view }: { view: WhatsOnView }) {
  const closing = useClosing();
  const moving = useMoved();
  const [problem, setProblem] = useState<string | null>(null);
  const say = (text: string | null) => {
    closing.setProblem(null);
    setProblem(text);
  };
  const onMoved = (moved: Moved) => {
    closing.dismiss();
    moving.onMoved(moved);
  };
  const actions: EntryActions = {
    onDone: (entry) => {
      setProblem(null);
      moving.dismiss();
      closing.close(entry);
    },
    renderLater: (entry) => <LaterMenu entry={entry} onProblem={say} onMoved={onMoved} />,
  };
  const unread = view.couldntReadVaults[0];
  return (
    <div className="mx-auto flex max-w-2xl flex-col gap-4">
      {(problem ?? closing.problem) && <FormError>{problem ?? closing.problem}</FormError>}
      {view.items.length === 0 ? (
        <p className="text-text-body">Nothing on. Enjoy it.</p>
      ) : (
        <ul className="flex flex-col gap-1.5">
          {view.items.map((entry) => (
            <Item key={entry.path} entry={entry} actions={actions} />
          ))}
        </ul>
      )}
      {view.couldntRead > 0 && unread && (
        <p className="text-sm">
          <Link to={`${calendarUrl(unread)}#couldnt-read`} className="inline-flex min-h-12 items-center font-medium text-accent-text underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-accent">
            {view.couldntRead === 1 ? "1 date I couldn't read" : `${view.couldntRead} dates I couldn't read`}
          </Link>
        </p>
      )}
      {closing.toast}
      {moving.toast}
    </div>
  );
}

/** One item, with ✓ and Later only where the person may write: What's on spans vaults of different roles. */
function Item({ entry, actions }: { entry: EntryView; actions: EntryActions }) {
  const writable = useMayChange(entry.path);
  return <EntryItem entry={entry} actions={writable ? actions : undefined} vault={entry.vault} />;
}
