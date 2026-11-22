// SPDX-License-Identifier: AGPL-3.0-or-later
// The first sync's merge plan (spec §6, "First sync"): what each side has, and what needs one choice.
import type { NotSynced, SyncState, Version } from "./ports";

export type Choice = NonNullable<SyncState["pendingChoice"]>;
export interface MergePlan { toUpload: string[]; toDownload: string[]; same: string[]; differing: string[]; notSynced: NotSynced[]; }
export interface FirstSyncSummary { uploaded: number; downloaded: number; unchanged: number; conflictCopies: number; notSynced: NotSynced[]; }

/** Sorts each path without a base into one of the plan's lists by its two versions (null: absent on that side). */
export function makePlan(paths: Iterable<[string, { local: Version | null; remote: Version | null }]>, notSynced: NotSynced[]): MergePlan {
  const plan: MergePlan = { toUpload: [], toDownload: [], same: [], differing: [], notSynced };
  for (const [path, { local, remote }] of paths) {
    if (local !== null && remote === null) plan.toUpload.push(path);
    else if (local === null && remote !== null) plan.toDownload.push(path);
    else if (local !== null && local === remote) plan.same.push(path);
    else if (local !== null) plan.differing.push(path);
  }
  return plan;
}
