// SPDX-License-Identifier: AGPL-3.0-or-later
// Every file type and large files, through MCP (spec 2026-11-18-files-design.md, PRs #254, #257, #259; brief, open
// point 4). Files go in through `Api.putFile`, as the web app's upload sends them, made in the test (the large ones
// too, never as fixtures), and are removed again at each test's end.
//
// Promises in scope (spec decision → test):
//  1. any path that isn't a note or a known format is a file, kept;
//     a known format must still hold its bytes                          → "a CSV, a text file and a script …",
//                                                                          "a .zip and a file without an extension …",
//                                                                          "a .png that holds no PNG …"
//  2. a file and a folder never share a name, refused with a sentence
//     naming the other (upload, write, mkdir, mv); folders with dots,
//     files without an extension, rm of one                             → "a file and a folder can't share a name …"
//     restore onto a name taken by the other kind: left out, it needs
//     a file and a folder to swap places in the trash (outside the
//     run's scope for @files, spec §2)
//  3. UTF-8 without NUL is text: cat prints it, rg searches it; any
//     other file is bytes: cat describes it in one line; text over
//     15 MB is bytes too; a rename doesn't change the media type        → "a CSV, a text file and a script …",
//                                                                          "a .zip and a file without an extension …",
//                                                                          "a text file over 15 MB …",
//                                                                          "a renamed text file …"
//     a note wins a link over a file of the same name (LinkResolver)   → "a note wins a link …"
//  4. files are served only as downloads: attachment, the stored media
//     type, nosniff and the sandbox policy, whatever the request asks  → "an .html file and a file of bytes are served …"
//     on /api/files; WebDAV GET is left out: the stack runs on plain HTTP and /dav answers 403 "WebDAV needs HTTPS"
//     (the brief's open point 2 keeps the stack's security settings); checkpoint files, the reader and AI links are
//     outside @files (spec §2: @publish, @readers, @deeplinks)
//  9. larger than 15 MB is kept; the download gives the same bytes      → "a file over 15 MB …", "an image over 15 MB …",
//                                                                          "a PDF over 15 MB …"
// 12. what reads a whole file says it keeps but doesn't read one over
//     15 MB: cat and img for an image, cat and pdf for a PDF            → "an image over 15 MB …", "a PDF over 15 MB …"
//     shown by the browser, streamed                                    → e2e/files/files.spec.ts "files #4"
// Left out:
//  5, 15 the web app: e2e/files/files.spec.ts.
//  6 publishing: spec §2 puts it under @publish.
//  7 the renaming rules beyond decision 3's media type: outside spec §2's @files scope (renaming is the move dialog's
//    and mv's, no promise of 0.6.0's beyond decision 3).
//  8 WebDAV parking: the stack runs on plain HTTP, and /dav answers 403 "WebDAV needs HTTPS".
//  10, 13, 14 chunks, their keys, streamed reads, the sweep: not observable from outside (brief, open point 4).
//  11 the 1 GiB bound and the quota: needs a body over 1 GiB or a changed admin bound on the stack.
//  Obsidian 0.3.1 and rclone: the spec's hand tests.
import { createHash, randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { encodePath, type Api } from "../support/api";
import { fixturesDir } from "../support/env";
import { expect, test as base } from "./fixtures";

type Put = {
  /** Writes a file (or a note, for `.md`) and removes it at the test's end, whatever happened. */
  put: (path: string, content: string | Buffer) => Promise<string>;
  /** A path a step may have written (one refused, or the target of a move): removed at the end if it is there. */
  track: (path: string) => string;
};

const test = base.extend<Put & { cleanup: string[] }>({
  // Every path is tried at the end, and one that is gone already (404) is fine; the first other failure is thrown once
  // all were tried, so one failure doesn't leave the rest behind.
  cleanup: async ({ aliceApi }, use) => {
    const paths: string[] = [];
    await use(paths);
    const failures: string[] = [];
    for (const path of paths.reverse()) {
      const response = await aliceApi.send("DELETE", `/api/${path.endsWith(".md") ? "notes" : "files"}/${encodePath(path)}`);
      if (!response.ok() && response.status() !== 404) failures.push(`DELETE ${path}: ${response.status()}`);
    }
    if (failures.length) throw new Error(failures.join("; "));
  },
  put: async ({ aliceApi, cleanup }, use) => {
    await use(async (path, content) => {
      cleanup.push(path);
      if (path.endsWith(".md")) await aliceApi.putNote(path, content.toString());
      else await aliceApi.putFile(path, typeof content === "string" ? Buffer.from(content, "utf8") : content);
      return path;
    });
  },
  track: async ({ cleanup }, use) => {
    await use((path) => {
      cleanup.push(path);
      return path;
    });
  },
});

const sha256 = (bytes: Buffer) => createHash("sha256").update(bytes).digest("hex");

/** The bytes the download gives (`GET /api/files/<path>`). */
async function download(api: Api, path: string) {
  const response = await api.send("GET", `/api/files/${encodePath(path)}`);
  expect(response.status(), `GET ${path}`).toBe(200);
  return response.body();
}

/** A file's upload as the web app sends it, for the refusals (`Api.putFile` throws on them). */
const upload = (api: Api, path: string, bytes: Buffer) =>
  api.send("PUT", `/api/files/${encodePath(path)}`, { data: bytes, headers: { "Content-Type": "application/octet-stream" } });

/** A one-line answer: checked short before it is compared, so a regression can't print 15 MB into the report. */
function oneLine(answer: { text: string }, what: string) {
  expect(answer.text.length, `${what}: a line, not the file`).toBeLessThan(300);
  return answer.text;
}

/** One byte over the bound (`AttachmentFormat.MaxBytes`, 15 MiB). */
const large = 15 * 1024 * 1024 + 1;
// The code's sentence (AttachmentFormat.NotRead). Decision 12's quote ("larger than 15 MB: kept, but its text isn't
// read") illustrates "an honest sentence"; PR #257's description ("says a large file is kept, but not read") matches
// the one built, so the built one is asserted.
const notRead = "larger than 15 MB: Kumbu² keeps it, but doesn't read it";
/** `Security.FileContentPolicy` (src/Kumbu.Server/Api/Security.cs:70). */
const fileContentPolicy = "sandbox; default-src 'none'; frame-ancestors 'none'";

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
  // A text file with the same word beside them: rg searches the folder, and finds it there only.
  const words = await put(`/Work/${folder}/words.txt`, "the lighthouse\n");

  const zipRead = await mcp.sh(`cat '${zip}'`);
  expect(zipRead.isError).toBe(false);
  expect(oneLine(zipRead, "cat archive.zip"), "one line, never the bytes").toBe("archive.zip: file, 2 KB");
  expect(oneLine(await mcp.sh(`cat '${bare}'`), "cat blob")).toBe("blob: file, 2 KB");

  const listed = await mcp.sh(`ls '/Work/${folder}'`);
  expect(listed.text).toBe("archive.zip  file 2 KB\nblob  file 2 KB\nwords.txt  text 15 B");

  expect(await mcp.sh(`rg -l lighthouse '/Work/${folder}'`), "only the text file").toEqual({ text: words, isError: false });
  // The download gives the bytes back as they were.
  expect(sha256(await download(aliceApi, zip))).toBe(sha256(bytes));
});

test("files: a .png that holds no PNG is refused; any other name takes any bytes @files", async ({ put, track, folder, aliceApi, mcp }) => {
  const fake = track(`/Work/${folder}/fake.png`);
  const refused = await upload(aliceApi, fake, Buffer.from("not a picture\n"));
  expect(refused.status()).toBe(400);
  expect(await refused.json()).toEqual({ error: "fake.png isn't a PNG image (its content doesn't match its name)." });
  // The same bytes under a name of no known format are a file.
  await put(`/Work/${folder}/fake.png.txt`, "not a picture\n");
  expect(await mcp.sh(`cat '/Work/${folder}/fake.png.txt'`)).toEqual({ text: "not a picture\n", isError: false });
});

test("files: a file and a folder can't share a name, either way round, with dots and without an extension @files", async ({ mcp, put, track, folder, aliceApi }) => {
  // A folder with a dot in its name (it could be a file's name), and a file without an extension (a folder's).
  await put(`/Work/${folder}/v1.2/Plan.md`, "# Plan\n");
  const data = await put(`/Work/${folder}/data`, Buffer.from([0x00, 0x01, 0x02, 0xff]));
  const other = await put(`/Work/${folder}/other`, Buffer.from([0x00, 0x03]));

  const onFolder = await upload(aliceApi, track(`/Work/${folder}/v1.2`), Buffer.from("a file where a folder is\n"));
  expect(onFolder.status()).toBe(409);
  expect(await onFolder.json()).toEqual({ error: `upload: /Work/${folder}/v1.2 is a folder; a file and a folder can't share a name` });

  const belowFile = await mcp.call("write", { path: track(`/Work/${folder}/data/Note.md`), content: "# Note\n" });
  expect(belowFile.isError).toBe(true);
  expect(belowFile.text).toBe(`write: /Work/${folder}/data is a file, not a folder; nothing can be kept below it`);

  const fileBelowFile = await upload(aliceApi, track(`/Work/${folder}/data/inner.csv`), Buffer.from("a,b\n"));
  expect(fileBelowFile.status()).toBe(409);
  expect(await fileBelowFile.json()).toEqual({ error: `upload: /Work/${folder}/data is a file, not a folder; nothing can be kept below it` });

  const mkdir = await mcp.sh(`mkdir '${data}'`);
  expect(mkdir).toEqual({ text: `mkdir: ${data}: File exists (it is a file)`, isError: true });

  // A file moved below a file is refused; moved onto a folder's name it goes into the folder, as mv does in a shell.
  const fileBelow = await mcp.sh(`mv '${other}' '${track(`${data}/other`)}'`);
  expect(fileBelow).toEqual({ text: `mv: ${data} is a file, not a folder; nothing can be kept below it`, isError: true });
  const intoFolder = await mcp.sh(`mv '${other}' '/Work/${folder}/v1.2'`);
  expect(intoFolder.isError).toBe(false);
  expect(intoFolder.text.split("\n")[0]).toBe(`moved ${other} to ${track(`/Work/${folder}/v1.2/other`)}; no links needed rewriting`);
  // A folder moved onto a file's name.
  const folderOnFile = await mcp.sh(`mv '/Work/${folder}/v1.2/' '${data}/'`);
  expect(folderOnFile).toEqual({ text: `mv: ${data} is a file, not a folder; nothing can be kept below it`, isError: true });

  // Both are still what they were: the folder lists its note and the file moved in, the file its bytes.
  expect((await mcp.sh(`ls '/Work/${folder}'`)).text).toBe("data  file 4 B\nv1.2/");
  expect((await mcp.sh(`ls '/Work/${folder}/v1.2'`)).text).toBe("other  file 2 B\nPlan.md");
  expect((await mcp.sh(`cat '${data}'`)).text).toBe("data: file, 4 B");

  // rm takes the file without an extension, and the folder stays.
  expect(await mcp.sh(`rm '${data}'`)).toEqual({
    text: `removed ${data}; it stays in the trash for 30 days ('restore ${data}' brings it back)`,
    isError: false,
  });
  expect((await mcp.sh(`ls '/Work/${folder}'`)).text).toBe("v1.2/");
});

test("files: a renamed text file keeps its media type: cat still prints it @files", async ({ mcp, put, track, folder }) => {
  const csv = await put(`/Work/${folder}/costs.csv`, "Item,Amount\nlantern,12.50\n");
  const dat = track(`/Work/${folder}/costs.dat`);
  const moved = await mcp.sh(`mv '${csv}' '${dat}'`);
  expect(moved.isError).toBe(false);
  expect(moved.text.split("\n")[0]).toBe(`moved ${csv} to ${dat}; no links needed rewriting`);
  expect(await mcp.sh(`cat '${dat}'`)).toEqual({ text: "Item,Amount\nlantern,12.50\n", isError: false });
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

test("files: an .html file and a file of bytes are served only as downloads, whatever the request asks @files", async ({ put, folder, aliceApi }) => {
  // A page that would run if a browser rendered it inline (stored XSS, decision 4).
  const html = await put(`/Work/${folder}/page.html`, "<!doctype html><title>x</title><script>document.title = 'ran'</script>\n");
  const bin = await put(`/Work/${folder}/data.bin`, Buffer.from([0x00, 0xff, 0x10]));
  for (const [path, type, name] of [
    [html, "text/plain; charset=utf-8", "page.html"],
    [bin, "application/octet-stream", "data.bin"],
  ]) {
    // Without ?download=1, and asking for a page.
    const response = await aliceApi.send("GET", `/api/files/${encodePath(path)}`, { headers: { Accept: "text/html" } });
    expect(response.status(), path).toBe(200);
    const headers = response.headers();
    expect(headers["content-disposition"], `${name}: a download`).toMatch(/^attachment\b/);
    expect(headers["content-disposition"]).toContain(name);
    expect(headers["content-type"], `${name}: its stored media type`).toBe(type);
    expect(headers["x-content-type-options"]).toBe("nosniff");
    expect(headers["content-security-policy"]).toBe(fileContentPolicy);
  }
});

test("files: a file over 15 MB is kept, described in one line, and downloaded with the same bytes @files", async ({ mcp, put, folder, aliceApi }) => {
  test.setTimeout(120_000);
  const bytes = randomBytes(large);
  const path = await put(`/Work/${folder}/big.bin`, bytes);

  const read = await mcp.sh(`cat '${path}'`);
  expect(read.isError).toBe(false);
  // A file of bytes is never read, whatever its size: cat's one line, no sentence (AttachmentText.Line).
  expect(oneLine(read, "cat big.bin")).toBe("big.bin: file, 15.0 MB");
  expect(sha256(await download(aliceApi, path)), "the same bytes back").toBe(sha256(bytes));
});

test("files: a text file over 15 MB is kept as bytes, not read as text @files", async ({ mcp, put, folder }) => {
  test.setTimeout(120_000);
  // UTF-8 without NUL, but larger than 15 MB: kept as bytes (decision 3), so neither printed nor searched.
  const line = "lantern,12.50\n";
  const path = await put(`/Work/${folder}/big.csv`, "x".repeat(large - line.length) + line);
  // A small text file with the same word: rg searches the folder, and finds it there only.
  const small = await put(`/Work/${folder}/small.csv`, line);
  expect(oneLine(await mcp.sh(`cat '${path}'`), "cat big.csv")).toBe("big.csv: file, 15.0 MB");
  const found = await mcp.sh(`rg -l lantern '/Work/${folder}'`);
  expect(oneLine(found, "rg -l"), "only the small file").toBe(small);
  expect(found.isError).toBe(false);
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
  expect(oneLine(read, "cat scan.png")).toBe(`scan.png: image (PNG), 8×8, 15.0 MB — ${notRead}`);

  const img = await mcp.sh(`img '${path}'`);
  expect(img.isError).toBe(true);
  expect(oneLine(img, "img")).toBe(`img: ${path} is ${notRead}`);

  expect(sha256(await download(aliceApi, path)), "the same bytes back").toBe(sha256(bytes));
});

test("files: a PDF over 15 MB is kept but not read by cat or pdf, and downloaded with the same bytes @files", async ({ mcp, put, folder, aliceApi }) => {
  test.setTimeout(120_000);
  // A real PDF, padded after its end to one byte over the bound.
  const pdf = readFileSync(join(fixturesDir, "tags", "recent", "Pump", "manual.pdf"));
  const bytes = Buffer.concat([pdf, randomBytes(large - pdf.length)]);
  const path = await put(`/Work/${folder}/manual.pdf`, bytes);

  const read = await mcp.sh(`cat '${path}'`);
  expect(read.isError).toBe(false);
  expect(oneLine(read, "cat manual.pdf")).toBe(`manual.pdf: PDF, 15.0 MB — ${notRead}`);

  for (const command of [`pdf '${path}'`, `pdf info '${path}'`, `pdf '${path}' 1`]) {
    const answer = await mcp.sh(command);
    expect(answer.isError, command).toBe(true);
    expect(oneLine(answer, command), command).toBe(`pdf: ${path} is ${notRead}`);
  }

  expect(sha256(await download(aliceApi, path)), "the same bytes back").toBe(sha256(bytes));
});
