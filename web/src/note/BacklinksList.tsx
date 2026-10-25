// SPDX-License-Identifier: AGPL-3.0-or-later
import { Link2 } from "lucide-react";
import { useNavigate } from "react-router-dom";
import { useBacklinks } from "../api/notes";
import { crumbOf, noteUrl, titleOf } from "../api/paths";

/** "Linked from": each note with a line linking here, once, in the server's order. */
export default function BacklinksList({ path }: { path: string }) {
  const backlinks = useBacklinks(path);
  const navigate = useNavigate();
  const sources = [...new Map((backlinks.data ?? []).map((b) => [b.path, b])).values()];
  if (sources.length === 0) return null;

  return (
    <section aria-label="Linked from" className="mt-[30px] border-t border-line pt-4">
      <h2 className="mb-1.5 font-mono text-[11px] font-medium tracking-[0.08em] text-dim uppercase">
        Linked from · {sources.length}
      </h2>
      <ul>
        {sources.map((b) => (
          <li key={b.path}>
            <button
              type="button"
              onClick={() => navigate(noteUrl(b.path))}
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
  );
}
