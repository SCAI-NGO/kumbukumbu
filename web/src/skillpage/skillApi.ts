// SPDX-License-Identifier: AGPL-3.0-or-later
// Plain fetch, not the app's client: nothing of the app comes with the skill page (isolation.test.ts), and every
// request goes to /api/k.

export interface SkillFile {
  index: number;
  /** Relative to the skill's folder. */
  path: string;
  mediaType: string;
  size: number;
}

export interface SharedSkill {
  /** The folder's name. */
  name: string;
  description: string;
  manifest: string;
  snapshotAt: string;
  files: SkillFile[];
  hasScripts: boolean;
  /** The copy's id, sent with the .zip: the one skill cookie may be another tab's skill's by then. */
  copy: string;
}

export type OpenResult = { kind: "skill"; skill: SharedSkill } | { kind: "none" } | { kind: "off" } | { kind: "busy" };

async function answer(response: Response): Promise<OpenResult> {
  if (response.ok) return { kind: "skill", skill: (await response.json()) as SharedSkill };
  if (response.status === 429) return { kind: "busy" };
  if (response.status === 404) {
    // The server's switch carries a type of its own; every other 404 (wrong, withdrawn, no cookie) is the same.
    const body: unknown = await response.json().catch(() => null);
    const off = !!body && typeof body === "object" && "type" in body && body.type === "publishing-off";
    return { kind: off ? "off" : "none" };
  }
  throw new Error(`The skill endpoint answered ${response.status}.`);
}

/** Opens the skill with the code; the server answers with the skill cookie, so a reload needs no code. */
export async function openWithCode(code: string): Promise<OpenResult> {
  return answer(
    await fetch("/api/k", { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code }) }),
  );
}

/** Opens the skill with the skill cookie alone. */
export async function openAgain(): Promise<OpenResult> {
  return answer(await fetch("/api/k", { credentials: "same-origin" }));
}

/** The skill `copy` as a .zip; the server sends it only if the skill cookie is still that copy's. */
export async function zip(copy: string): Promise<Blob> {
  const response = await fetch("/api/k/zip", { credentials: "same-origin", headers: { "X-Kumbu-Copy": copy } });
  if (!response.ok) throw new Error(`The .zip answered ${response.status}.`);
  return response.blob();
}
