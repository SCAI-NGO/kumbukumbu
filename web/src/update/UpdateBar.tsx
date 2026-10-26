// SPDX-License-Identifier: AGPL-3.0-or-later
import { BarButton, useTimeout } from "../note/ChangedElsewhere";
import { useEditor } from "../state/editor";
import { tryReload } from "./apply";
import { useUpdate } from "./store";

const barClass = "flex min-h-11 flex-wrap items-center gap-x-1 rounded-[12px] border border-accent bg-surface-2 py-1 pr-1.5 pl-3.5 text-[13px] text-text shadow-lg";

/**
 * What the update watcher has to say, over the page: the bar while Write mode holds the reload back, the sentence
 * when a reload didn't bring the new version, and a short notice after one did.
 */
export default function UpdateBar() {
  const available = useUpdate((s) => s.available);
  const failed = useUpdate((s) => s.failed);
  const updated = useUpdate((s) => s.updated);
  const mode = useEditor((s) => s.mode);
  useTimeout(updated !== null, 4_000, () => useUpdate.getState().markUpdated(null));

  let content = null;
  if (failed) {
    content = (
      <p role="alert" className={barClass}>
        {`Kumbu² couldn't load version ${failed.semver}. Reload the page, or ask your admin.`}
      </p>
    );
  } else if (available && mode === "write") {
    content = (
      <section aria-label="Update" className={barClass}>
        <span className="min-w-0 flex-1 py-2">{`Kumbu² ${available.semver} is available`}</span>
        <BarButton onClick={() => void tryReload("button")}>Update</BarButton>
      </section>
    );
  } else if (updated) {
    content = (
      <p role="status" aria-label="Updated" className={barClass}>
        {`Updated to ${updated}`}
      </p>
    );
  }
  if (!content) return null;
  return <div className="pointer-events-auto fixed inset-x-3 top-[max(env(safe-area-inset-top),0.75rem)] z-50 mx-auto max-w-md">{content}</div>;
}
