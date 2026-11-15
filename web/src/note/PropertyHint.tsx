// SPDX-License-Identifier: AGPL-3.0-or-later
import { useIsMutating } from "@tanstack/react-query";

/** Said by the app after a write of the note's properties that didn't happen, whatever started it. */
export const CHANGED_ELSEWHERE = "This note changed elsewhere; try again.";

/**
 * What a tap on Move to properties came to: a key the properties have already, properties that can't be read, keys
 * that wouldn't read back once moved (all refusals: the tap would only meet them again), or a sentence after which it
 * may be tried again (a conflict, a failure).
 */
export type MoveSaid = { exists: string } | { unreadable: true } | { unwritable: true } | { text: string };

// The accent's text colour as the edge: line is barely there on the hint's ground; accent itself falls under 3:1 in
// some light schemes, accent-text never under 4.5:1.
const actionClass = "min-h-12 rounded-[10px] border border-accent-text bg-surface px-4 font-medium text-accent-text disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent";

/**
 * Above a block in the body that looks like frontmatter but renders as a rule and a heading (spec §2.2): where
 * properties go, and with `onMove` (write) the way to move them there, waiting while a save of the note is under way.
 * A refusal takes the button's place; for a key that is there already, with `onWrite` (Write is on), the way to change
 * it there.
 */
export default function PropertyHint({ path, onMove, onWrite, said }: { path: string; onMove?: () => void; onWrite?: () => void; said: MoveSaid | null }) {
  const saving = useIsMutating({ mutationKey: ["save", path] }) > 0;
  const refused = !!said && !("text" in said);
  return (
    <div role="note" className="my-3 flex flex-col items-start gap-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
      <p>Properties go at the very top of a note.</p>
      {said && (
        <p role="status">
          {"exists" in said ? (
            <>
              <code className="rounded-[4px] bg-surface px-[5px] py-px font-mono text-[0.85em]">{said.exists}</code> is already in the properties: change it there.
            </>
          ) : "unreadable" in said ? (
            "The properties at the top can't be read here, so nothing was moved."
          ) : "unwritable" in said ? (
            "That can't be written as properties here, so nothing was moved."
          ) : (
            said.text
          )}
        </p>
      )}
      {said && "exists" in said && onWrite && (
        <button type="button" onClick={onWrite} className={actionClass}>
          Change it in Write mode
        </button>
      )}
      {onMove && !refused && (
        <button
          type="button"
          disabled={saving}
          onClick={onMove}
          className={actionClass}
        >
          Move to properties
        </button>
      )}
    </div>
  );
}
