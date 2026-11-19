// SPDX-License-Identifier: AGPL-3.0-or-later
// Every file type and large files, through MCP (spec 2026-11-18-files-design.md, PRs #254, #257, #259; brief, open
// point 4). Files go in through `Api.putFile`, as the web app's upload sends them, made in the test (the large ones
// too, never as fixtures), and are removed again at each test's end.
//
// Promises in scope (spec decision → test):
//  1. any path that isn't a note or a known format is a file, kept      → "a CSV, a text file and a script …",
//                                                                          "a .zip and a file without an extension …"
//  2. a file and a folder never share a name, refused with a sentence
//     naming the other; folders with dots, files without an extension  → "a file and a folder can't share a name …"
//  3. UTF-8 without NUL is text: cat prints it, rg searches it; any
//     other file is bytes: cat describes it in one line; text over
//     15 MB is bytes too                                                → "a CSV, a text file and a script …",
//                                                                          "a .zip and a file without an extension …",
//                                                                          "a text file over 15 MB …"
//     a note wins a link over a file of the same name (LinkResolver)   → "a note wins a link …"
//  9. larger than 15 MB is kept; the download gives the same bytes      → "a file over 15 MB …", "an image over 15 MB …"
// 12. what reads a whole file says it keeps but doesn't read one over
//     15 MB (img, and cat for an image)                                 → "an image over 15 MB …"
// Left out: 4 (headers on every serving endpoint: FileApiTests and the WebDAV tests assert them), 5 (the web app:
// e2e/files/files.spec.ts), 6 (publishing: @publish), 7 (renaming rules: unit tests), 8 (WebDAV parking), 10–11 and
// 13–14 (chunks, keys, the bound, the sweep: not observable from outside, brief open point 4), 15 (the web app's
// upload: e2e/files/files.spec.ts); Obsidian 0.3.1 and rclone (spec's hand tests).
import { createHash, randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { encodePath, type Api } from "../support/api";
import { fixturesDir } from "../support/env";
import { expect, test as base } from "./fixtures";

const test = base.extend<{ put: (path: string, content: string | Buffer) => Promise<string> }>({
  // Writes a file (or a note, for `.md`) and removes it at the test's end, whatever happened.
  put: async ({ aliceApi }, use) => {
    const written: string[] = [];
    await use(async (path, content) => {
      if (path.endsWith(".md")) await aliceApi.putNote(path, content.toString());
      else await aliceApi.putFile(path, typeof content === "string" ? Buffer.from(content, "utf8") : content);
      written.push(path);
      return path;
    });
    for (const path of written.reverse()) await remove(aliceApi, path);
  },
});

async function remove(api: Api, path: string) {
  if (path.endsWith(".md")) await api.deleteNote(path);
  else {
    const response = await api.send("DELETE", `/api/files/${encodePath(path)}`);
    if (!response.ok()) throw new Error(`DELETE file ${path}: ${response.status()}`);
  }
}

const sha256 = (bytes: Buffer) => createHash("sha256").update(bytes).digest("hex");

/** The bytes the download gives (`GET /api/files/<path>`). */
async function download(api: Api, path: string) {
  const response = await api.send("GET", `/api/files/${encodePath(path)}`);
  expect(response.status(), `GET ${path}`).toBe(200);
  return response.body();
}

/** One byte over the bound (`AttachmentFormat.MaxBytes`, 15 MiB). */
const large = 15 * 1024 * 1024 + 1;
const notRead = "larger than 15 MB: Kumbu² keeps it, but doesn't read it";

test("files: a CSV, a text file and a script are text: cat prints them, rg finds a word in them @files", async ({ mcp, put, folder }) => {
  const csv = await put(`/Work/${folder}/costs.csv`, "Item,Amount\nlantern,12.50\n");
  const txt = await put(`/Work/${folder}/notes.txt`, "The keeper trims the lantern.\n");
  const sh = await put(`/Work/${folder}/light.sh`, "#!/bin/sh\necho lantern\n");

  for (const [path, content] of [
    [csv, "Item,Amount\nlantern,12.50\n"],
    [txt, "The keeper trims the lantern.\n"],
    [sh, "#!/bin/sh\necho lantern\n"],
  ]) {
    const read = await mcp.sh(`cat '${path}'`);
    expect(read.isError, path).toBe(false);
    expect(read.text, `cat prints ${path} as it is`).toBe(content);
  }

  const found = await mcp.sh(`rg -n lantern '/Work/${folder}'`);
  expect(found.isError).toBe(false);
  expect(found.text.split("\n").sort()).toEqual(
    [`${csv}:2:lantern,12.50`, `${txt}:1:The keeper trims the lantern.`, `${sh}:2:echo lantern`].sort(),
  );
});

test("files: a .zip and a file without an extension are bytes: cat describes them in one line, rg leaves them out @files", async ({ mcp, put, folder, aliceApi }) => {
  // A ZIP's magic bytes, a NUL, and a word rg would find if it read the bytes as text.
  const bytes = Buffer.concat([Buffer.from([0x50, 0x4b, 0x03, 0x04, 0x00, 0xff]), Buffer.from(" lighthouse "), randomBytes(2042)]);
  const zip = await put(`/Work/${folder}/archive.zip`, bytes);
  const bare = await put(`/Work/${folder}/blob`, bytes);

  const zipRead = await mcp.sh(`cat '${zip}'`);
  expect(zipRead.isError).toBe(false);
  expect(zipRead.text, "one line, never the bytes").toBe("archive.zip: file, 2 KB");
  const bareRead = await mcp.sh(`cat '${bare}'`);
  expect(bareRead.text).toBe("blob: file, 2 KB");

  const listed = await mcp.sh(`ls '/Work/${folder}'`);
  expect(listed.text).toBe("archive.zip  file 2 KB\nblob  file 2 KB");

  const found = await mcp.sh(`rg -l lighthouse '/Work/${folder}'`);
  expect(found.text, "a file of bytes isn't searched").not.toContain(folder);
  // The download gives the bytes back as they were.
  expect(sha256(await download(aliceApi, zip))).toBe(sha256(bytes));
});

test("files: a file and a folder can't share a name, either way round, with dots and without an extension @files", async ({ mcp, put, folder, aliceApi }) => {
  // A folder with a dot in its name (it could be a file's name), and a file without an extension (a folder's).
  await put(`/Work/${folder}/v1.2/Plan.md`, "# Plan\n");
  const data = await put(`/Work/${folder}/data`, Buffer.from([0x00, 0x01, 0x02, 0xff]));

  const onFolder = await aliceApi.send("PUT", `/api/files/${encodePath(`/Work/${folder}/v1.2`)}`, {
    data: Buffer.from("a file where a folder is\n"),
    headers: { "Content-Type": "application/octet-stream" },
  });
  expect(onFolder.status()).toBe(409);
  expect(await onFolder.json()).toEqual({ error: `upload: /Work/${folder}/v1.2 is a folder; a file and a folder can't share a name` });

  const belowFile = await mcp.call("write", { path: `/Work/${folder}/data/Note.md`, content: "# Note\n" });
  expect(belowFile.isError).toBe(true);
  expect(belowFile.text).toBe(`write: /Work/${folder}/data is a file, not a folder; nothing can be kept below it`);

  const fileBelowFile = await aliceApi.send("PUT", `/api/files/${encodePath(`/Work/${folder}/data/inner.csv`)}`, {
    data: Buffer.from("a,b\n"),
    headers: { "Content-Type": "application/octet-stream" },
  });
  expect(fileBelowFile.status()).toBe(409);
  expect(await fileBelowFile.json()).toEqual({ error: `upload: /Work/${folder}/data is a file, not a folder; nothing can be kept below it` });

  // Both are still what they were: the folder lists its note, the file its bytes.
  expect((await mcp.sh(`ls '/Work/${folder}'`)).text).toBe("data  file 4 B\nv1.2/");
  expect((await mcp.sh(`ls '/Work/${folder}/v1.2'`)).text).toBe("Plan.md");
  expect((await mcp.sh(`cat '${data}'`)).text).toBe("data: file, 4 B");
});

test("files: a note wins a link over a file of the same name @files", async ({ mcp, put, folder }) => {
  // Names with the folder in them: links resolve vault-wide.
  const note = await put(`/Work/${folder}/notes/${folder}-Plan.md`, "# Plan\n");
  await put(`/Work/${folder}/files/${folder}-Plan`, "a file under the note's name\n");
  const csv = await put(`/Work/${folder}/files/${folder}-data.csv`, "a,b\n");
  const hub = await put(`/Work/${folder}/Hub.md`, `# Hub\n\n[[${folder}-Plan]] and [[${folder}-data.csv]]\n`);

  const links = await mcp.sh(`links '${hub}'`);
  expect(links.isError).toBe(false);
  // The note, not the file, under the note's name; the file where no note carries its name.
  expect(links.text.split("\n").slice(0, 2)).toEqual([`3: [[${folder}-Plan]] → ${note}`, `3: [[${folder}-data.csv]] → ${csv}`]);
});

test("files: a file over 15 MB is kept, described in one line, and downloaded with the same bytes @files", async ({ mcp, put, folder, aliceApi }) => {
  test.setTimeout(120_000);
  const bytes = randomBytes(large);
  const path = await put(`/Work/${folder}/big.bin`, bytes);

  const read = await mcp.sh(`cat '${path}'`);
  expect(read.isError).toBe(false);
  // A file of bytes is never read, whatever its size: cat's one line, no sentence (AttachmentText.Line).
  expect(read.text).toBe("big.bin: file, 15.0 MB");
  expect(sha256(await download(aliceApi, path)), "the same bytes back").toBe(sha256(bytes));
});

test("files: a text file over 15 MB is kept as bytes, not read as text @files", async ({ mcp, put, folder }) => {
  test.setTimeout(120_000);
  // UTF-8 without NUL, but larger than 15 MB: kept as bytes (decision 3), so neither printed nor searched.
  const line = "lantern,12.50\n";
  const path = await put(`/Work/${folder}/big.csv`, "x".repeat(large - line.length) + line);
  expect((await mcp.sh(`cat '${path}'`)).text).toBe("big.csv: file, 15.0 MB");
  expect(await mcp.sh(`rg -l lantern '/Work/${folder}'`), "nothing found below the folder").toEqual({ text: "", isError: false });
  expect(await mcp.sh(`rg -l lantern '${path}'`)).toEqual({
    text: `rg: ${path} is an attachment; rg searches the text of notes and text files only`,
    isError: true,
  });
});

test("files: an image over 15 MB is kept but not read by cat or img, and downloaded with the same bytes @files", async ({ mcp, put, folder, aliceApi }) => {
  test.setTimeout(120_000);
  // A real PNG, padded after its end to one byte over the bound: the format is read from its first bytes.
  const png = readFileSync(join(fixturesDir, "tags", "recent", "Pump", "scan.png"));
  const bytes = Buffer.concat([png, randomBytes(large - png.length)]);
  const path = await put(`/Work/${folder}/scan.png`, bytes);

  const read = await mcp.sh(`cat '${path}'`);
  expect(read.isError).toBe(false);
  // The fixture is 8×8 pixels: its size is read from its header.
  expect(read.text).toBe(`scan.png: image (PNG), 8×8, 15.0 MB — ${notRead}`);

  const img = await mcp.sh(`img '${path}'`);
  expect(img.isError).toBe(true);
  expect(img.text).toBe(`img: ${path} is ${notRead}`);

  expect(sha256(await download(aliceApi, path)), "the same bytes back").toBe(sha256(bytes));
});
