// SPDX-License-Identifier: AGPL-3.0-or-later
import { ArrowLeft, X } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { writerText } from "../api/activity";
import { errorText } from "../api/error-text";
import { fileKindOfEntry } from "../api/notes";
import { noteUrl, viewUrl, type LightboxState } from "../api/paths";
import { fileKindIcon } from "../components/fileKindIcon";
import FlagChips, { Chip } from "../flags/FlagChips";
import { useDiscreet } from "../discreet/state";
import FlagIcon from "../flags/FlagIcon";
import { ago } from "../note/AgentBanner";
import type { FlagFilter } from "../shell/treeFilter";
import { useRecent, type RecentItem } from "./api";

/** How many rows come at first, and how many more each Show more adds. */
const PAGE = 50;
/** The words are sent once typing pauses, not for every key. */
const TYPING_MS = 250;

/**
 * `/recent`: the notes and attachments of every vault the session shows, by their last change, newest first (spec §4).
 * Narrowed by words over the path with its vault, by vault (all on at first) and by flag. The filters live here alone,
 * never in the address or the device's storage: a discreet vault's name must reach neither.
 */
export default function RecentPage() {
  const queryClient = useQueryClient();
  const [typed, setTyped] = useState("");
  const [words, setWords] = useState("");
  const [flag, setFlag] = useState<FlagFilter | null>(null);
  const [skip, setSkip] = useState<string[]>([]);
  const [limit, setLimit] = useState(PAGE);
  const field = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (typed === words) return;
    const timer = setTimeout(() => {
      setWords(typed);
      setLimit(PAGE);
    }, TYPING_MS);
    return () => clearTimeout(timer);
  }, [typed, words]);
  // Hiding or showing the discreet vaults starts the filters again: the words or a vault switched off may name one hidden
  // now, and the page stays mounted. What was answered before goes too: the rows kept for the filters as they were
  // before would be shown again, for as long as the page asks.
  useEffect(
    () =>
      useDiscreet.subscribe((s, previous) => {
        if (s.names === previous.names && s.revealed === previous.revealed) return;
        setTyped("");
        setWords("");
        setFlag(null);
        setSkip([]);
        setLimit(PAGE);
        void queryClient.resetQueries({ queryKey: ["recent"] });
      }),
    [queryClient],
  );
  const recent = useRecent({ words, flag, skip, limit });
  const view = recent.data;

  const onFlag = (next: FlagFilter | null) => {
    setFlag(next);
    setLimit(PAGE);
  };
  const toggle = (vault: string) => {
    setSkip((off) => (off.includes(vault) ? off.filter((v) => v !== vault) : [...off, vault]));
    setLimit(PAGE);
  };
  const filtered = words.trim() !== "" || flag !== null || skip.length > 0;

  return (
    <div className="relative flex h-dvh flex-col bg-bg">
      <header className="flex flex-none items-center gap-1 border-b border-line px-2 py-1.5 lg:px-3">
        <Link to="/" aria-label="Back to the notes" className="flex size-12 flex-none items-center justify-center rounded-[10px] text-text-body focus-visible:outline-2 focus-visible:outline-accent">
          <ArrowLeft size={20} aria-hidden />
        </Link>
        <h1 className="min-w-0 flex-1 truncate px-2 text-[20px] font-bold text-text">Recent changes</h1>
      </header>
      <main className="min-h-0 flex-1 overflow-y-auto px-3 py-3 lg:px-4">
        <div className="mx-auto flex max-w-2xl flex-col gap-3">
          <div role="search" aria-label="Find in recent changes" className="flex flex-col gap-1">
            <div className="flex items-center gap-1">
              <input
                ref={field}
                type="search"
                aria-label="Find by name"
                value={typed}
                onChange={(e) => setTyped(e.target.value)}
                placeholder="Find by name"
                autoComplete="off"
                autoCapitalize="none"
                autoCorrect="off"
                spellCheck={false}
                enterKeyHint="search"
                className="h-12 min-w-0 flex-1 rounded-full border border-line bg-bg px-4 text-[16px] text-text outline-none placeholder:text-muted focus:border-accent [&::-webkit-search-cancel-button]:hidden"
              />
              {/* The browser's own ✕ is too small a target: this one instead, while there is something to clear. */}
              {typed !== "" && (
                <button
                  type="button"
                  aria-label="Clear"
                  onClick={() => {
                    setTyped("");
                    field.current?.focus();
                  }}
                  className="flex size-12 flex-none items-center justify-center rounded-[8px] text-muted focus-visible:outline-2 focus-visible:outline-accent"
                >
                  <X size={20} aria-hidden />
                </button>
              )}
            </div>
            {view && view.vaults.length > 0 && (
              <div role="group" aria-label="Vaults" className="flex flex-wrap gap-x-1">
                {view.vaults.map((vault) => (
                  <Chip key={vault} pressed={!skip.includes(vault)} onClick={() => toggle(vault)}>
                    {vault}
                  </Chip>
                ))}
              </div>
            )}
            <FlagChips flag={flag} onFlag={onFlag} />
          </div>
          {recent.isPending && <p className="text-sm text-muted">Loading …</p>}
          {recent.isError && <p className="text-text">{errorText(recent.error)}</p>}
          {view &&
            (view.items.length === 0 ? (
              <p role="status" className="text-text-body">
                {filtered ? "Nothing matches that." : "Nothing changed here yet."}
              </p>
            ) : (
              <>
                <ul aria-label="Recent changes" className="flex flex-col">
                  {view.items.map((item) => (
                    <Row key={item.path} item={item} attachments={view.items.filter((i) => fileKindOfEntry(i) !== "note").map((i) => i.path)} />
                  ))}
                </ul>
                {view.more && (
                  <button
                    type="button"
                    disabled={recent.isFetching}
                    onClick={() => setLimit((l) => l + PAGE)}
                    className="flex min-h-12 items-center justify-center self-center rounded-full border border-line px-5 text-[15px] font-medium text-accent-text focus-visible:outline-2 focus-visible:outline-accent disabled:opacity-60"
                  >
                    Show more
                  </button>
                )}
              </>
            ))}
        </div>
      </main>
    </div>
  );
}

/** A note opens as the note; an attachment in the lightbox, which pages through the attachments listed, as the tree's do. */
function Row({ item, attachments }: { item: RecentItem; attachments: string[] }) {
  const kind = fileKindOfEntry(item);
  const Icon = fileKindIcon[kind];
  const note = kind === "note";
  return (
    <li>
      <Link
        to={note ? noteUrl(item.path) : viewUrl(item.path)}
        state={note ? undefined : ({ paths: attachments } satisfies LightboxState)}
        className="flex min-h-12 items-start gap-3 rounded-[10px] px-2 py-2 hover:bg-surface focus-visible:outline-2 focus-visible:outline-accent"
      >
        <Icon size={18} strokeWidth={1.8} aria-hidden className="mt-0.5 flex-none text-muted" />
        <span className="flex min-w-0 flex-1 flex-col">
          <span className="flex min-w-0 items-center gap-1.5">
            <span className="min-w-0 truncate text-[15px] text-text">{item.title}</span>
            <FlagIcon flag={item.flag} size={14} />
          </span>
          <span className="flex min-w-0 text-[13px] text-muted">
            <span className="min-w-0 truncate">{item.folder}</span>
            <span className="flex-none whitespace-pre">
              {" · "}
              {writerText(item)} · <time dateTime={item.writtenAt} title={new Date(item.writtenAt).toLocaleString("en-GB")}>{ago(item.writtenAt)}</time>
            </span>
          </span>
        </span>
      </Link>
    </li>
  );
}
