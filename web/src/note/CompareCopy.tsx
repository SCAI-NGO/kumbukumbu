// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, useState } from "react";
import { titleOf } from "../api/paths";
import type { components } from "../api/schema";
import { Sheet } from "../components/Sheet";
import CompareVersions from "../shell/CompareVersions";

type Copied = components["schemas"]["CopiedNoteView"];

/**
 * A published copy beside the note now (Published overview design, step 2): the note's text at the version the copy
 * was made from against its text now, in the comparison the activity uses. For a bundle, one note of it at a time,
 * the start first. The left side is the note's own text, before the copy's filter took comments and the like out.
 */
export default function CompareCopy({ start, copied, onClose }: { start: string; copied: Copied[]; onClose(): void }) {
  const [chosen, setChosen] = useState(0);
  const id = useId();
  const note = copied[chosen] ?? copied[0];
  return (
    <Sheet title={`Compare ${titleOf(start)}`} onClose={onClose}>
        <p className="text-sm text-text-body">The note as the published copy was made from it, beside the note now. Update copy publishes the right side.</p>
        {copied.length > 1 && (
          <div className="flex flex-col gap-1">
            <label htmlFor={id} className="text-sm text-muted">
              Note of the copy
            </label>
            <select
              id={id}
              value={chosen}
              onChange={(e) => setChosen(Number(e.target.value))}
              className="min-h-11 rounded-[10px] border border-line bg-bg px-3 text-text"
            >
              {copied.map((c, i) => (
                <option key={c.path} value={i}>
                  {c.path}
                </option>
              ))}
            </select>
          </div>
        )}
        {note && (
          <CompareVersions key={note.path} path={note.path} version={note.version} before={null} trashed={false} beforeLost={false} initial="now" versionLabel="Published copy" fixed />
        )}
    </Sheet>
  );
}
