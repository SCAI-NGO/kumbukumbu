// SPDX-License-Identifier: AGPL-3.0-or-later
import { archiveNoticeable, freeSentence, fullSentence, percentUsed, storageText, useAccountStorage, useStorageLimited } from "../api/storage";

/** From this share of her limit on, the owner is told (WebDAV design §3.10). */
const WARN_FROM = 90;

/**
 * A quiet line in the shell once the owner's own storage is 90 % full, before a write is refused; nothing without a
 * limit, and nothing if the figure can't be read (Settings says that). It names the archive beside the trash once the
 * archive holds a tenth of the used space. The figure is of her own vaults only: a vault
 * shared with her counts against its owner, who is told in her own app. It asks only when the session says a limit
 * applies, since counting reads every document of her vaults, and at most once a minute.
 */
export default function StorageNotice() {
  const limited = useStorageLimited();
  const storage = useAccountStorage({ enabled: limited === true, staleTime: 60_000, refetchOnWindowFocus: false });
  const percent = storage.data ? percentUsed(storage.data) : null;
  if (!storage.data || percent == null || percent < WARN_FROM) return null;
  const archive = archiveNoticeable(storage.data);
  return (
    <p role="status" className="mx-3 mb-2 rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-sm text-text">
      {percent >= 100
        ? fullSentence(Number(storage.data.limit), archive)
        : `Your storage is ${percent} % full: ${storageText(Number(storage.data.used))} of ${storageText(Number(storage.data.limit))}. ${freeSentence(archive)}`}
    </p>
  );
}
