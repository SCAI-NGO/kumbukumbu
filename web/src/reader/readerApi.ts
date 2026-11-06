// SPDX-License-Identifier: AGPL-3.0-or-later
// Plain fetch, not the app's client: nothing of the app comes with the reader page (isolation.test.ts).

export interface ReaderFile {
  index: number;
  mediaType: string;
}

export interface ReaderCopy {
  title: string;
  markdown: string;
  snapshotAt: string;
  files: ReaderFile[];
  /** The copy's id, sent with each of its files: the one reader cookie may be another tab's copy's by then. */
  copy: string;
}

export type ReadResult = { kind: "copy"; copy: ReaderCopy } | { kind: "none" } | { kind: "off" } | { kind: "busy" };

async function answer(response: Response): Promise<ReadResult> {
  if (response.ok) return { kind: "copy", copy: (await response.json()) as ReaderCopy };
  if (response.status === 429) return { kind: "busy" };
  if (response.status === 404) {
    // The server's switch carries a type of its own; every other 404 (wrong, withdrawn, no cookie) is the same.
    const body: unknown = await response.json().catch(() => null);
    const off = !!body && typeof body === "object" && "type" in body && body.type === "publishing-off";
    return { kind: off ? "off" : "none" };
  }
  throw new Error(`The reader endpoint answered ${response.status}.`);
}

/** Opens the copy with the code; the server answers with the reader cookie, so a reload needs no code. */
export async function readWithCode(code: string): Promise<ReadResult> {
  return answer(
    await fetch("/api/s", { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code }) }),
  );
}

/** Opens the copy with the reader cookie alone. */
export async function readAgain(): Promise<ReadResult> {
  return answer(await fetch("/api/s", { credentials: "same-origin" }));
}

/** File `index` of the copy `copy`; the server opens it only if the reader cookie is still that copy's. */
export async function readFile(copy: string, index: number): Promise<Blob> {
  const response = await fetch(`/api/s/files/${index}`, { credentials: "same-origin", headers: { "X-Kumbu-Copy": copy } });
  if (!response.ok) throw new Error(`The file answered ${response.status}.`);
  return response.blob();
}
