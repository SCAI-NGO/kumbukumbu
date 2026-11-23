// SPDX-License-Identifier: AGPL-3.0-or-later
// The web app's offline writes into the copy (offline mode design §3, work package 7), over the core's NoteWriter.
import "fake-indexeddb/auto";
import type { VaultMirror } from "eidolon-core/store/vault";
import { versionOf } from "eidolon-core/engine/version";
import { afterEach, beforeEach, expect, test } from "vitest";
import { HttpError } from "../api/client";
import { offlineCopy } from "./testCopy";
import { createOffline, createUntitledOffline, draftsFor, offlineWrites, onOfflineWrite, saveOffline, setOfflineWrites, trashOffline } from "./writing";

let vaults: Record<string, VaultMirror>;
let written: [string, string][];

beforeEach(async () => {
  ({ vaults, written } = await offlineCopy());
  await vaults.Work!.files().writeIf("Plan.md", null, "one");
  await vaults.Work!.files().writeIf("projects/Untitled.md", null, "taken");
  await vaults.Work!.files().writeIf("projects/b.md", null, "b");
  written.length = 0;
});

afterEach(() => setOfflineWrites(null));

test("is off until the host turns it on", () => {
  expect(offlineWrites()).not.toBeNull();
  setOfflineWrites(null);
  expect(offlineWrites()).toBeNull();
});

test("a save writes into the copy, tells the engine and the listeners, and answers the new version", async () => {
  let heard = 0;
  const stop = onOfflineWrite(() => heard++);
  const result = await saveOffline("/Work/Plan.md", "two", await versionOf("one"));
  expect(result).toEqual({ saved: { version: await versionOf("two") } });
  expect(await vaults.Work!.files().read("Plan.md")).toBe("two");
  expect(written).toEqual([["v-work", "Plan.md"]]);
  expect(heard).toBe(1);
  stop();
});

test("a save on an older version answers the text there, as a 409", async () => {
  const result = await saveOffline("/Work/Plan.md", "mine", "aaaaaaaaaaaa");
  expect(result).toEqual({ conflict: { content: "one", version: await versionOf("one") } });
});

test("a read-only vault refuses as the server does, with its code", async () => {
  const error = await saveOffline("/Team/x.md", "x", "aaaaaaaaaaaa").catch((e: unknown) => e);
  expect(error).toBeInstanceOf(HttpError);
  expect((error as HttpError).status).toBe(403);
  expect((error as HttpError).code).toBe("read-only");
});

test("a vault not kept on this device needs a connection", async () => {
  await expect(saveOffline("/Archive/x.md", "x", "aaaaaaaaaaaa")).rejects.toThrow(/needs a connection/);
});

test("a new note is made in the copy; a taken path answers a 409", async () => {
  expect(await createOffline("/Work/Idea.md", "")).toBe("/Work/Idea.md");
  expect(await vaults.Work!.files().read("Idea.md")).toBe("");
  const error = await createOffline("/Work/Plan.md", "").catch((e: unknown) => e);
  expect((error as HttpError).status).toBe(409);
});

test("Untitled takes the first free name in its folder", async () => {
  expect(await createUntitledOffline("/Work/projects")).toBe("/Work/projects/Untitled 2.md");
  expect(await vaults.Work!.files().read("projects/Untitled 2.md")).toBe("# ");
  expect(await createUntitledOffline("/Work")).toBe("/Work/Untitled.md");
});

test("trashing takes the note out of the copy and answers the note to go on to", async () => {
  expect(await trashOffline("/Work/projects/Untitled.md")).toBe("/Work/projects/b.md");
  expect(vaults.Work!.entry("projects/Untitled.md")?.local).toBeUndefined();
  expect(await trashOffline("/Work/projects/b.md")).toBeNull();
});

test("each vault has one set of drafts, which the engine's files read", async () => {
  const drafts = draftsFor("v-work");
  expect(draftsFor("v-work")).toBe(drafts);
  const close = drafts.open("Plan.md", { text: () => "typing", replaced: () => {} });
  expect(await vaults.Work!.files(drafts).read("Plan.md")).toBe("typing");
  close();
});
