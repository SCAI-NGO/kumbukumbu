// SPDX-License-Identifier: AGPL-3.0-or-later
// Waiting for search by meaning by the product's own signals (spec §3): `similar` says a source "isn't indexed yet"
// and counts the notes not compared in its last line; `/api/similar` answers `preparing` or `unindexed` with a pending
// count. Never a fixed sleep: polled with a bound the caller states.
import { expect } from "@playwright/test";
import { encodePath, type Api } from "./api";

/** The first index on a fresh stack waits for the model to load after its download. */
export const firstIndex = 180_000;
/** Later notes are embedded within seconds of their write. */
export const nextIndex = 60_000;

const parent = (path: string) => path.replace(/\/[^/]+$/, "");

/**
 * Until `similar <note> <its folder>` answers for every note, with no note of the folder waiting ("isn't indexed yet",
 * "… indexed yet and not compared"). Through the console: read-only, in the API's session.
 */
export async function waitIndexed(api: Api, notes: string[], timeout = firstIndex) {
  for (const note of notes) {
    await expect
      .poll(
        async () => {
          const { text } = await api.console(`similar "${note}" "${parent(note)}"`);
          // The heading or error line and the counts line only: a note's own text may say anything.
          const lines = text.trim().split("\n");
          const said = `${lines[0]}\n${lines[lines.length - 1]}`;
          return /isn't indexed yet|has no indexed text yet|indexed yet and not compared|getting ready/.test(said) ? "waiting" : "indexed";
        },
        { message: `${note} and its folder are embedded`, timeout, intervals: [1000] },
      )
      .toBe("indexed");
  }
}

export type SimilarNotes = { state: "ready" | "unindexed" | "preparing" | "off"; notes: { path: string; line: number; text: string; score: number }[]; pending: number };

/** `GET /api/similar/{vault}/{path}`, as the Links tab asks it. */
export const similarNotes = (api: Api, path: string) => api.json<SimilarNotes>("GET", `/api/similar/${encodePath(path)}`);

/** Until the Links tab's query is `ready` with nothing pending in the vault. */
export async function waitSimilarReady(api: Api, path: string, timeout = nextIndex) {
  await expect
    .poll(async () => {
      const view = await similarNotes(api, path);
      return `${view.state} ${view.pending}`;
    }, { message: `the similar notes of ${path} are ready`, timeout, intervals: [1000] })
    .toBe("ready 0");
}
