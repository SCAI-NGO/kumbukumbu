// SPDX-License-Identifier: AGPL-3.0-or-later
import { api, call, HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { toBatches, type Skipped, type UploadFile } from "../upload/planUpload";
import { vaultKey, type ImportVault } from "./readImport";

/**
 * What importing a vault of the ZIP means: `exists` when the user has a vault of that name (as the server tells names
 * apart), `target` the name in the server's spelling. Paths below the vault: `unchanged` notes are there with this
 * content already, `differing` ones with other content, `fresh` ones not at all. `seen`: the version each differing
 * note had when compared, so "Replace all" replaces only what the user was asked about.
 */
export interface VaultPlan {
  vault: ImportVault;
  target: string;
  exists: boolean;
  differing: string[];
  unchanged: string[];
  fresh: string[];
  seen: Map<string, string>;
}

/** For the differing notes of an existing vault: replace them all (the history keeps the old versions) or keep them all. */
export type Choice = "replace" | "skip";

/**
 * `vaults`: those imported in full or in part; `kept`: existing notes left as they were (`Skip all`, changed after the
 * comparison, or found there by the server); `skipped`: files of the ZIP and of the server's answers that weren't
 * written, as `Vault/path`; `failed`: vaults that couldn't be made or whose upload stopped, with the server's sentence
 * and how many notes of it were written before.
 */
export interface ImportSummary {
  vaults: number;
  imported: number;
  replaced: number;
  kept: number;
  unchanged: number;
  skipped: Skipped[];
  failed: { vault: string; reason: string; written: number }[];
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
 * Compares each vault of the ZIP with the user's vault of that name, matched as the server matches names (NFC, trimmed,
 * any case). `versions`: the vault's version list, or null when it wasn't read, which makes every note count as
 * differing (nothing is assumed equal).
 */
export function planImport(vaults: ImportVault[], existing: { name: string; versions: Map<string, string> | null }[]): VaultPlan[] {
  return vaults.map((vault) => {
    const match = existing.find((e) => vaultKey(e.name) === vaultKey(vault.name));
    const plan: VaultPlan = { vault, target: match?.name ?? vault.name, exists: !!match, differing: [], unchanged: [], fresh: [], seen: new Map() };
    for (const note of vault.notes) {
      if (!match) plan.fresh.push(note.path);
      else if (!match.versions) plan.differing.push(note.path);
      else {
        const version = match.versions.get(note.path);
        if (version === undefined) plan.fresh.push(note.path);
        else if (version === note.version) plan.unchanged.push(note.path);
        else {
          plan.differing.push(note.path);
          plan.seen.set(note.path, version);
        }
      }
    }
    return plan;
  });
}

const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** "3 vaults, 412 notes imported · ✓ no conflicts", or the counts of what was replaced, kept or failed. */
export function summaryText(s: ImportSummary): string {
  const head = `${count(s.vaults, "vault", "vaults")}, ${count(s.imported, "note", "notes")} imported`;
  const partly = s.failed.filter((f) => f.written > 0).length;
  const failed = s.failed.length - partly;
  const parts = [
    s.replaced > 0 && `${s.replaced} replaced`,
    s.kept > 0 && `${s.kept} kept as ${s.kept === 1 ? "it was" : "they were"}`,
    partly > 0 && `${count(partly, "vault", "vaults")} partly imported`,
    failed > 0 && `${count(failed, "vault", "vaults")} failed`,
  ].filter(Boolean);
  return parts.length ? [head, ...parts].join(" · ") : `${head} · ✓ no conflicts`;
}

// The server's sentence for a note it kept because `skip` met it; such a note is a conflict, settled by keeping it.
const EXISTS = "a note exists at this path";
const CHANGED = "changed after the comparison; kept as it is";
const SIGNED_OUT = "You were signed out; sign in and import the rest again.";

class SignedOut extends Error {}

/**
 * Imports the planned vaults one after the other: a missing vault is made, then its notes go to its root in batches
 * the server takes. Fresh notes go with `skip`, so a note that appeared meanwhile isn't replaced unasked; a batch of
 * them that met a change meanwhile (409) is sent once more. Differing notes go only with the choice `replace`, and
 * only those still at the version the user was asked about: the version list is read again first, and again after
 * a 409, which is never answered by sending the same replace batch blindly. Unchanged notes aren't sent. Any other
 * failure stops that vault, and the next one goes on; a session that ends stops the run. `onProgress`: notes done of
 * all to send.
 */
export async function runImport(
  plans: VaultPlan[],
  choices: Map<string, Choice>,
  importApi: ImportApi,
  onProgress: (done: number, total: number) => void,
): Promise<ImportSummary> {
  const summary: ImportSummary = { vaults: 0, imported: 0, replaced: 0, kept: 0, unchanged: 0, skipped: [], failed: [] };
  const pick = (plan: VaultPlan, paths: string[]) => {
    const wanted = new Set(paths);
    return plan.vault.notes.filter((n) => wanted.has(n.path)).map(({ path, content }) => ({ path, content }));
  };
  const work = plans.map((plan) => {
    const replace = choices.get(plan.vault.name) === "replace" && plan.differing.length > 0;
    const fresh = toBatches(pick(plan, plan.fresh));
    return { plan, replace, fresh, toSend: plan.fresh.length - fresh.skipped.length + (replace ? plan.differing.length : 0) };
  });
  const total = work.reduce((sum, w) => sum + w.toSend, 0);
  let done = 0;
  onProgress(done, total);

  for (let i = 0; i < work.length; i++) {
    const { plan, replace, fresh, toSend } = work[i];
    const name = plan.target;
    const skip = (list: Skipped[]) => summary.skipped.push(...list.map((s) => ({ path: `${name}/${s.path}`, reason: s.reason })));
    const keep = (paths: string[]) => {
      summary.kept += paths.length;
      skip(paths.map((path) => ({ path, reason: CHANGED })));
    };
    skip([...plan.vault.skipped, ...fresh.skipped]);
    let sent = 0;
    let written = 0;
    const advance = (n: number) => {
      sent += n;
      done += n;
      onProgress(done, total);
    };
    const send = async (files: UploadFile[], onConflict: Choice) => {
      const result = await importApi.upload(name, files, onConflict);
      written += Number(result.written) + Number(result.replaced);
      summary.imported += Number(result.written);
      summary.replaced += Number(result.replaced);
      summary.kept += result.skipped.filter((s) => s.reason === EXISTS).length;
      skip(result.skipped);
      advance(files.length);
    };
    /** The differing notes of `paths` still at the version compared against; the others are kept and counted. */
    const stillAsSeen = async (paths: string[]) => {
      const now = await importApi.versions(name);
      const still = paths.filter((p) => now.get(p) === plan.seen.get(p));
      const changed = paths.filter((p) => now.get(p) !== plan.seen.get(p));
      keep(changed);
      if (changed.length) advance(changed.length);
      return still;
    };
    try {
      try {
        if (!plan.exists) await importApi.createVault(name);
        for (const files of fresh.batches) {
          try {
            await send(files, "skip");
          } catch (err) {
            if (!(err instanceof HttpError && err.status === 409)) throw err;
            await send(files, "skip");
          }
        }
        if (replace) {
          const differing = toBatches(pick(plan, await stillAsSeen(plan.differing)));
          skip(differing.skipped);
          for (const files of differing.batches) {
            try {
              await send(files, "replace");
            } catch (err) {
              if (!(err instanceof HttpError && err.status === 409)) throw err;
              const still = new Set(await stillAsSeen(files.map((f) => f.path)));
              const again = files.filter((f) => still.has(f.path));
              if (again.length) await send(again, "replace");
            }
          }
        }
      } catch (err) {
        if (err instanceof HttpError && err.status === 401) throw new SignedOut();
        throw err;
      }
    } catch (err) {
      const signedOut = err instanceof SignedOut;
      summary.failed.push({ vault: name, reason: signedOut ? SIGNED_OUT : errorText(err), written });
      if (written > 0) summary.vaults++;
      // What won't be sent of this vault moves the bar on, so it still ends full.
      if (toSend > sent) advance(toSend - sent);
      if (signedOut) {
        for (const rest of work.slice(i + 1)) summary.failed.push({ vault: rest.plan.target, reason: SIGNED_OUT, written: 0 });
        break;
      }
      continue;
    }
    summary.vaults++;
    summary.unchanged += plan.unchanged.length;
    if (!replace) summary.kept += plan.differing.length;
  }
  return summary;
}
