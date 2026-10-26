// SPDX-License-Identifier: AGPL-3.0-or-later
import { api, call, HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { toBatches, type Skipped, type UploadFile } from "../upload/planUpload";
import type { ImportVault } from "./readImport";

/**
 * What importing a vault of the ZIP means: `exists` when the user has a vault of that name. Paths below the vault:
 * `unchanged` notes are there with this content already, `differing` ones with other content, `fresh` ones not at all.
 */
export interface VaultPlan {
  vault: ImportVault;
  exists: boolean;
  differing: string[];
  unchanged: string[];
  fresh: string[];
}

/** For the differing notes of an existing vault: replace them all (the history keeps the old versions) or keep them all. */
export type Choice = "replace" | "skip";

/**
 * `vaults`: those imported in full or in part; `kept`: differing notes left as they were (`Skip all`); `skipped`: files
 * of the ZIP and of the server's answers that weren't notes it could write, as `Vault/path`; `failed`: vaults that
 * couldn't be made or whose upload stopped, with the server's sentence.
 */
export interface ImportSummary {
  vaults: number;
  imported: number;
  replaced: number;
  kept: number;
  unchanged: number;
  skipped: Skipped[];
  failed: { vault: string; reason: string }[];
}

/** The calls an import makes, so a test can stand in for the server. */
export interface ImportApi {
  createVault(name: string): Promise<void>;
  /** Every note of the vault: path below the vault → version. */
  versions(vault: string): Promise<Map<string, string>>;
  upload(vault: string, files: UploadFile[], onConflict: Choice): Promise<{ written: number | string; replaced: number | string; skipped: Skipped[] }>;
}

export const serverImportApi: ImportApi = {
  async createVault(name) {
    await call(api.POST("/api/vaults", { body: { name } }));
  },
  async versions(vault) {
    const answer = await call(api.GET("/api/vaults/{vault}/versions", { params: { path: { vault } } }));
    // The API's paths are from the root of the tree: "/Work/a.md".
    const prefix = `/${vault}/`;
    return new Map(answer.notes.filter((n) => n.path.startsWith(prefix)).map((n) => [n.path.slice(prefix.length), n.version]));
  },
  upload: (vault, files, onConflict) => call(api.POST("/api/upload/{vault}/{folder}", { params: { path: { vault, folder: "" } }, body: { files, onConflict } })),
};

/**
 * Compares each vault of the ZIP with the user's vault of that name. `versions`: the vault's version list, or null
 * when it wasn't read, which makes every note count as differing (nothing is assumed equal).
 */
export function planImport(vaults: ImportVault[], existing: { name: string; versions: Map<string, string> | null }[]): VaultPlan[] {
  return vaults.map((vault) => {
    const match = existing.find((e) => e.name === vault.name);
    const plan: VaultPlan = { vault, exists: !!match, differing: [], unchanged: [], fresh: [] };
    for (const note of vault.notes) {
      if (!match) plan.fresh.push(note.path);
      else if (!match.versions) plan.differing.push(note.path);
      else {
        const version = match.versions.get(note.path);
        if (version === undefined) plan.fresh.push(note.path);
        else (version === note.version ? plan.unchanged : plan.differing).push(note.path);
      }
    }
    return plan;
  });
}

const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** "3 vaults, 412 notes imported · ✓ no conflicts", or the counts of what was replaced, kept or failed. */
export function summaryText(s: ImportSummary): string {
  const head = `${count(s.vaults, "vault", "vaults")}, ${count(s.imported, "note", "notes")} imported`;
  const parts = [
    s.replaced > 0 && `${s.replaced} replaced`,
    s.kept > 0 && `${s.kept} kept as ${s.kept === 1 ? "it was" : "they were"}`,
    s.failed.length > 0 && `${count(s.failed.length, "vault", "vaults")} failed`,
  ].filter(Boolean);
  return parts.length ? [head, ...parts].join(" · ") : `${head} · ✓ no conflicts`;
}

/**
 * Imports the planned vaults one after the other: a missing vault is made, then its notes go to its root in batches
 * the server takes. Fresh notes go with `skip`, so a note that appeared meanwhile isn't replaced unasked; differing
 * ones only with the choice `replace`, and with it. Unchanged notes aren't sent. A batch that met a change meanwhile
 * (409) is sent once more; any other failure stops that vault, and the next one goes on. `onProgress`: notes sent of
 * all to send.
 */
export async function runImport(
  plans: VaultPlan[],
  choices: Map<string, Choice>,
  importApi: ImportApi,
  onProgress: (done: number, total: number) => void,
): Promise<ImportSummary> {
  const summary: ImportSummary = { vaults: 0, imported: 0, replaced: 0, kept: 0, unchanged: 0, skipped: [], failed: [] };
  const work = plans.map((plan) => {
    const replace = choices.get(plan.vault.name) === "replace";
    const pick = (paths: string[]) => {
      const wanted = new Set(paths);
      return plan.vault.notes.filter((n) => wanted.has(n.path)).map(({ path, content }) => ({ path, content }));
    };
    const fresh = toBatches(pick(plan.fresh));
    const differing = toBatches(replace ? pick(plan.differing) : []);
    const steps = [
      ...fresh.batches.map((files) => ({ files, onConflict: "skip" as const })),
      ...differing.batches.map((files) => ({ files, onConflict: "replace" as const })),
    ];
    return { plan, replace, steps, skipped: [...plan.vault.skipped, ...fresh.skipped, ...differing.skipped] };
  });
  const total = work.reduce((sum, w) => sum + w.steps.reduce((n, s) => n + s.files.length, 0), 0);
  let done = 0;
  onProgress(done, total);

  for (const { plan, replace, steps, skipped } of work) {
    const name = plan.vault.name;
    const skip = (list: Skipped[]) => summary.skipped.push(...list.map((s) => ({ path: `${name}/${s.path}`, reason: s.reason })));
    skip(skipped);
    const toSend = steps.reduce((n, s) => n + s.files.length, 0);
    let sent = 0;
    try {
      if (!plan.exists) await importApi.createVault(name);
      for (const step of steps) {
        let result;
        try {
          result = await importApi.upload(name, step.files, step.onConflict);
        } catch (err) {
          if (!(err instanceof HttpError && err.status === 409)) throw err;
          result = await importApi.upload(name, step.files, step.onConflict);
        }
        summary.imported += Number(result.written);
        summary.replaced += Number(result.replaced);
        skip(result.skipped);
        sent += step.files.length;
        done += step.files.length;
        onProgress(done, total);
      }
    } catch (err) {
      summary.failed.push({ vault: name, reason: errorText(err) });
      // What won't be sent of this vault moves the bar on, so it still ends full.
      done += toSend - sent;
      if (toSend > sent) onProgress(done, total);
      continue;
    }
    summary.vaults++;
    summary.unchanged += plan.unchanged.length;
    if (!replace) summary.kept += plan.differing.length;
  }
  return summary;
}
