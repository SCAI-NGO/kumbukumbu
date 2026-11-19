// SPDX-License-Identifier: AGPL-3.0-or-later
import { Link2 } from "lucide-react";
import { useBacklinks } from "../api/notes";
import { crumbOf, titleOf } from "../api/paths";
import { useOpenNote } from "../shell/openInPane";
import UnlinkedMentions from "./UnlinkedMentions";

/**
 * "Linked from": each note with a line linking here, once, in the server's order. At the note's bottom it is set off
 * from the text by a gap and a rule; in the side panel (`panel`) it starts at the top.
 */
export default function BacklinksList({ path, panel = false }: { path: string; panel?: boolean }) {
  const backlinks = useBacklinks(path);
  const openNote = useOpenNote();
  const sources = [...new Map((backlinks.data ?? []).map((b) => [b.path, b])).values()];
  const mentions = <UnlinkedMentions path={path} linked={sources.length > 0} panel={panel} />;
  if (sources.length === 0) return mentions;

  return (
    <>
      <section aria-label="Linked from" className={panel ? undefined : "mt-[30px] border-t border-line pt-4"}>
        <h2 className="mb-1.5 font-mono text-[11px] font-medium tracking-[0.08em] text-dim uppercase">
          Linked from · {sources.length}
        </h2>
        <ul>
          {sources.map((b) => (
            <li key={b.path}>
              <button
                type="button"
                onClick={() => openNote(b.path)}
                className="flex min-h-12 w-full items-center gap-2.5 border-b border-line px-1 text-left text-text focus-visible:outline-2 focus-visible:outline-accent"
              >
                <Link2 size={18} strokeWidth={1.8} aria-hidden className="flex-none text-accent-text" />
                <span className="min-w-0 flex-1 truncate">{titleOf(b.path)}</span>
                {b.ambiguous && <span className="flex-none font-mono text-[11px] text-dim">ambiguous</span>}
                <span className="max-w-[40%] flex-none truncate font-mono text-[11px] text-dim">{crumbOf(b.path)}</span>
              </button>
            </li>
          ))}
        </ul>
      </section>
      {mentions}
    </>
  );
}
