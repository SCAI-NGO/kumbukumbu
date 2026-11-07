// SPDX-License-Identifier: AGPL-3.0-or-later
import { fullSentence, percentUsed, storageText, useAccountStorage } from "../api/storage";

/** From this share of her limit on, the owner is told (WebDAV design §3.10). */
const WARN_FROM = 90;

/**
 * A quiet line in the shell once the owner's own storage is 90 % full, before a write is refused; nothing without a
 * limit, and nothing if the figure can't be read (Settings says that). The figure is of her own vaults only: a vault
 * shared with her counts against its owner, who is told in her own app.
 */
export default function StorageNotice() {
  const storage = useAccountStorage();
  const percent = storage.data ? percentUsed(storage.data) : null;
  if (!storage.data || percent == null || percent < WARN_FROM) return null;
  return (
    <p role="status" className="mx-3 mb-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
      {percent >= 100
        ? fullSentence(Number(storage.data.limit))
        : `Your storage is ${percent} % full: ${storageText(Number(storage.data.used))} of ${storageText(Number(storage.data.limit))}. Empty the trash or ask your admin for more.`}
    </p>
  );
}
