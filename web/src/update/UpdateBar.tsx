// SPDX-License-Identifier: AGPL-3.0-or-later
import { BarButton, useTimeout } from "../note/ChangedElsewhere";
import { useEditor } from "../state/editor";
import { tryReload } from "./apply";
import { useUpdate } from "./store";

const barClass = "flex min-h-11 flex-none flex-wrap items-center gap-x-1 border-b border-line bg-surface pr-1.5 pl-4 text-[13px] text-text";
const Dot = () => <span aria-hidden className="size-2 flex-none rounded-full bg-accent" />;

/**
 * What the update watcher has to say, in the page's flow like the changed-elsewhere bar (never over the top bar):
 * the shell puts it under its top bar, the router's root above every other page. The bar while an open editor
 * holds the reload back, the sentence when a reload didn't bring the new version, a short notice after one did.
 */
export default function UpdateBar() {
  const available = useUpdate((s) => s.available);
  const failed = useUpdate((s) => s.failed);
  const updated = useUpdate((s) => s.updated);
  // An editor is open (its guard is set), and no text a session end kept is waiting: only then can Update save first.
  const editing = useEditor((s) => s.beforeRead !== null && s.draft === null);
  useTimeout(updated !== null, 4_000, () => useUpdate.getState().markUpdated(null));

  if (failed) {
    return (
      <div role="alert" className={barClass}>
        <Dot />
        <span className="min-w-0 flex-1 py-2 pl-1.5">{`Kumbu² couldn't load version ${failed.semver}. Reload the page, or ask your admin.`}</span>
        <BarButton onClick={() => useUpdate.getState().dismissFailed()}>Dismiss</BarButton>
      </div>
    );
  }
  if (available && editing) {
    return (
      <section aria-label="Update" className={barClass}>
        <Dot />
        <span className="min-w-0 flex-1 py-2 pl-1.5">{`Kumbu² ${available.semver} is available`}</span>
        <BarButton onClick={() => void tryReload("button")}>Update</BarButton>
      </section>
    );
  }
  if (updated) {
    return (
      <p role="status" aria-label="Updated" className={`${barClass} py-2`}>
        <Dot />
        <span className="pl-1.5">{`Updated to ${updated}`}</span>
      </p>
    );
  }
  return null;
}
