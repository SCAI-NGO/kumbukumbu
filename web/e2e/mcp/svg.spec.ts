// SPDX-License-Identifier: AGPL-3.0-or-later
// SVG as an image, as an agent writes, reads and looks at it (docs/superpowers/specs/2026-11-17-svg-as-image-design.md;
// PR #228), and as the server hands it out: from /api/files and from a published copy, under the sandbox policy.
//
// Promises in scope, and the test that covers each:
// 1. `write` takes an `.svg` whole: no version creates, a version replaces; its size from the root's width and height,
//    else its viewBox, one side and a viewBox as a browser has it (decisions 5, 6; "One side and a viewBox")
//    → "svg: write takes an SVG whole …".
// 2. `cat` prints the attachment's line, then the source (decision 7) → the same test.
// 3. `img` draws it through the sidecar at the size step, enlarged too, and `--crop` counts in the stored size
//    (decisions 4, 5; "The sidecar draws an SVG on a route of its own") → "svg: img draws it at the size step …".
// 4. Checked as XML: the root must be `svg` in the SVG namespace or none; a DTD is ignored, never resolved, so a file
//    using an entity it declares is refused as not well-formed; the refusal says what is wrong (decisions 2, 6;
//    "A file whose DTD declares entities") → "svg: write refuses what isn't an SVG …".
// 5. `edit` and `replace` refuse with "replace it whole with write" (decision 6) → the same test.
// 6. `rg` doesn't search it (decision 7) → "svg: write takes an SVG whole …" (a note with the same word is found).
// 9. No size in the file: none is stored, and `img` draws it as a square (decision 5; "Without a stored size")
//    → "svg: an SVG without a size …".
// 10. Up to 15 MB like every attachment, refused above with the sentence why (decision 1; AttachmentFormat.cs:199)
//    → "svg: an SVG over 15 MB is refused …".
// 11. An SVG image isn't moved into a skill's folder, where `.svg` is a text file ("Decided while building")
//    → "svg: an SVG image isn't moved into a skill's folder".
// 7. A script in an SVG is kept as written, and the file comes with `sandbox; default-src 'none'` and `nosniff` from
//    /api/files (decision 3) → "svg: a script is kept, and /api/files serves it under the sandbox policy".
// 8. Publishing strips an SVG: comments, `metadata` and an editor's own namespaces go, the drawing stays; the reader's
//    file comes under the same policy (decision 3; "Publishing strips an SVG") → "svg: a published SVG …".
// Left out: the no-fetch of external references and the child process killed at its time limit (decision 4, "Changed
// while building"): the sidecar's own tests (`pdf/test/`), and an SSRF probe needs a listener the stack can't reach;
// `.svg` as a text file inside a skill's folder (decision 1): a folder below `_skills/` that holds any file is a skill
// (SkillDirectory.cs:11-12), listed in every handshake's instructions while it exists, which would disturb @memory's
// exact assertions on them; /dav (decision 3): needs a WebDAV share and its password; the web app's `<img>`:
// e2e/svg/svg.spec.ts.
import { request } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { Api, encodePath, guarded, Mcp } from "../support/api";
import { baseURL } from "../support/env";
import { reserveReaderRead } from "../support/session";
import { expect, redact, test } from "./fixtures";

const sandbox = "sandbox; default-src 'none'; frame-ancestors 'none'";

/** A drawing of `width` × `height` with a rect of its own id, `extra` inside the root. */
const drawing = (id: string, width = 120, height = 60, extra = "") =>
  `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">` +
  `<rect id="e2e-${id}" width="${width}" height="${height}" fill="#2a6"/>${extra}</svg>\n`;

// What each test wrote, removed after it.
let written: string[] = [];

async function write(mcp: Mcp, path: string, content: string, version?: string) {
  written.push(path);
  const answer = await mcp.call("write", { path, content, ...(version ? { version } : {}) });
  expect(answer.isError, answer.text).toBe(false);
  return { first: answer.text.split("\n")[0], version: /^version of .+: ([0-9a-f]{12})$/m.exec(answer.text)?.[1] };
}

/** `img` through MCP: its text and the one image, its PNG's width and height read from the header. */
async function img(mcp: Mcp, command: string) {
  const { status, message } = await mcp.raw("tools/call", { name: "sh", arguments: { command } }, "MCP sh img");
  expect(status).toBe(200);
  const content = (message?.result as { content: { type: string; text?: string; data?: string; mimeType?: string }[] }).content;
  expect(
    content.map((c) => c.type),
    "a line of text, then the image",
  ).toEqual(["text", "image"]);
  const png = Buffer.from(content[1].data!, "base64");
  expect(content[1].mimeType).toBe("image/png");
  expect(png.subarray(0, 8).toString("hex"), "a PNG").toBe("89504e470d0a1a0a");
  return { text: content[0].text!, width: png.readUInt32BE(16), height: png.readUInt32BE(20), data: content[1].data! };
}

test.afterEach(async ({ mcp }) => {
  if (written.length > 0) await mcp.sh(`rm ${[...new Set(written)].map((p) => `"${p}"`).join(" ")}`);
  written = [];
});

test("svg: write takes an SVG whole, cat prints its source, a version replaces it @svg", async ({ mcp, folder }) => {
  const id = folder.slice(-6);
  const path = `/Work/${folder}/drawing ${id}.svg`;
  const source = drawing(id);
  const made = await write(mcp, path, source);
  expect(made.first).toBe(`created ${path}`);

  const read = await mcp.sh(`cat "${path}"`);
  expect(read.isError, read.text).toBe(false);
  expect(read.text.split(`\n\nversion of ${path}: `)[0]).toBe(`drawing ${id}.svg: image (SVG), 120×60, ${Buffer.byteLength(source)} B\n${source}`);
  expect(read.text).toContain(`\n\nversion of ${path}: ${made.version}\n`);

  // No version: refused, as for a note; with it, replaced. One side and a viewBox: 800 × 400.
  expect(await mcp.call("write", { path, content: source })).toEqual({
    text: `write: ${path} already exists (version ${made.version}); to overwrite it, pass that version`,
    isError: true,
  });
  const wide = `<svg width="800" viewBox="0 0 100 50"><circle cx="50" cy="25" r="20"/></svg>`;
  expect((await write(mcp, path, wide, made.version)).first).toBe(`wrote ${path}`);
  expect((await mcp.sh(`cat "${path}"`)).text.split("\n").slice(0, 2)).toEqual([`drawing ${id}.svg: image (SVG), 800×400, ${wide.length} B`, wide]);
  expect((await mcp.sh(`ls -l /Work/${folder}`)).text).toMatch(new RegExp(`^drawing ${id}\\.svg  image 800×400 ${wide.length} B  \\d{4}-\\d\\d-\\d\\d \\d\\d:\\d\\d UTC$`));

  // rg doesn't search it, SVG text being mostly coordinates; a note with the same word it finds.
  const note = `/Work/${folder}/Circle ${id}.md`;
  await write(mcp, note, "# Circle\n\nA circle of friends.\n");
  expect(await mcp.sh(`rg circle /Work/${folder}`)).toEqual({ text: `${note}:3:A circle of friends.`, isError: false });
});

test("svg: img draws it at the size step, enlarged too, and --crop counts in its stored size @svg", async ({ mcp, folder }) => {
  const id = folder.slice(-6);
  const path = `/Work/${folder}/drawing ${id}.svg`;
  const { version } = await write(mcp, path, drawing(id));

  const medium = await img(mcp, `img "${path}"`);
  expect(medium.text.split("\n")[0]).toBe(`${path} (version ${version}): image (SVG), original 120×60 px; medium: 1549×774 px PNG, ${/PNG, (\d+ KB)$/m.exec(medium.text)?.[1]}`);
  expect([medium.width, medium.height]).toEqual([1549, 774]);

  const crop = await img(mcp, `img "${path}" --crop 0,0,60,30 --size small`);
  expect(crop.text.split("\n")[0]).toMatch(new RegExp(`^${escape(path)} \\(version ${version}\\): image \\(SVG\\), original 120×60 px; crop 0,0,60,30; small: 768×384 px PNG, \\d+ KB$`));
  expect([crop.width, crop.height]).toEqual([768, 384]);

  // What it draws is what the file says: another colour, another picture of the same size.
  const other = `/Work/${folder}/other ${id}.svg`;
  await write(mcp, other, drawing(id).replace("#2a6", "#c33"));
  const red = await img(mcp, `img "${other}"`);
  expect([red.width, red.height]).toEqual([1549, 774]);
  expect(red.data === medium.data, "a red drawing renders unlike a green one").toBe(false);

  // Beyond its stored size: refused as for any image.
  const outside = await mcp.sh(`img "${path}" --crop 100,0,60,30`);
  expect(outside).toEqual({ text: "img: the crop 100,0,60,30 lies outside the image (120×60 px)", isError: true });
});

test("svg: write refuses what isn't an SVG, and edit and replace point to write @svg", async ({ mcp, folder }) => {
  const id = folder.slice(-6);
  const at = (name: string) => `/Work/${folder}/${name} ${id}.svg`;
  const refused = (name: string, content: string) => mcp.call("write", { path: at(name), content });

  expect(await refused("page", "<html><body/></html>")).toEqual({ text: `write: page ${id}.svg is not an SVG: the root element is html`, isError: true });
  expect(await refused("foreign", `<svg xmlns="http://example.com/x"/>`)).toEqual({
    text: `write: foreign ${id}.svg is not an SVG: the root element svg is in the namespace http://example.com/x`,
    isError: true,
  });
  const broken = await refused("broken", "<svg><rect></svg>");
  expect(broken.isError).toBe(true);
  expect(broken.text).toMatch(new RegExp(`^write: broken ${id}\\.svg is not an SVG: it isn't well-formed XML \\(.+\\)$`));
  // The DTD is ignored, never resolved: an entity it declares is undeclared, so the file isn't well-formed.
  const entity = await refused("entity", `<?xml version="1.0"?>\n<!DOCTYPE svg [<!ENTITY x "boom">]>\n<svg xmlns="http://www.w3.org/2000/svg">&x;</svg>`);
  expect(entity.isError).toBe(true);
  expect(entity.text).toMatch(new RegExp(`^write: entity ${id}\\.svg is not an SVG: it isn't well-formed XML \\(Reference to undeclared entity 'x'\\..*\\)$`));
  // An editor's export with a declaration, an external DTD and a comment: taken, sized from its viewBox.
  const exported = `\uFEFF<?xml version="1.0"?>\n<!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">\n<!-- exported -->\n<svg viewBox="0 0 10 20"/>\n`;
  expect((await write(mcp, at("export"), exported)).first).toBe(`created ${at("export")}`);
  expect((await mcp.sh(`cat "${at("export")}"`)).text.split("\n")[0]).toBe(`export ${id}.svg: image (SVG), 10×20, ${Buffer.byteLength(exported)} B`);

  // Nothing of the refused files was kept.
  expect((await mcp.sh(`ls /Work/${folder}`)).text).toBe(`export ${id}.svg  image 10×20 ${Buffer.byteLength(exported)} B`);

  const { version } = await write(mcp, at("drawing"), drawing(id));
  for (const tool of ["edit", "replace"]) {
    expect(await mcp.call(tool, { path: at("drawing"), version, old_text: "#2a6", new_text: "#c33" })).toEqual({
      text: `${tool}: drawing ${id}.svg is an SVG; replace it whole with write`,
      isError: true,
    });
  }
});

test("svg: an SVG without a size stores none, and img draws it as a square @svg", async ({ mcp, folder }) => {
  const id = folder.slice(-6);
  const path = `/Work/${folder}/sizeless ${id}.svg`;
  const source = `<svg xmlns="http://www.w3.org/2000/svg"><circle cx="5" cy="5" r="3"/></svg>`;
  const { version } = await write(mcp, path, source);
  expect((await mcp.sh(`cat "${path}"`)).text.split("\n").slice(0, 2)).toEqual([`sizeless ${id}.svg: image (SVG), ${source.length} B`, source]);
  const square = await img(mcp, `img "${path}" --size small`);
  expect(square.text.split("\n")[0]).toMatch(new RegExp(`^${escape(path)} \\(version ${version}\\): image \\(SVG\\), original 300×300 px; small: 632×632 px PNG, \\d+ KB$`));
  expect([square.width, square.height]).toEqual([632, 632]);
});

test("svg: an SVG over 15 MB is refused with the reason @svg", async ({ aliceApi, folder, mcp }) => {
  const id = folder.slice(-6);
  const path = `/Work/${folder}/big ${id}.svg`;
  // Well-formed, one byte over 15 MB: a comment pads it.
  const head = `<svg xmlns="http://www.w3.org/2000/svg"><!--`;
  const tail = `--></svg>`;
  const bytes = Buffer.concat([Buffer.from(head), Buffer.alloc(15 * 1024 * 1024 + 1 - head.length - tail.length, 0x61), Buffer.from(tail)]);
  expect(bytes.length).toBe(15 * 1024 * 1024 + 1);
  const put = await aliceApi.send("PUT", `/api/files/${encodePath(path)}`, { data: bytes, headers: { "Content-Type": "application/octet-stream" } });
  expect(put.status()).toBe(413);
  expect(await put.json()).toEqual({ error: `big ${id}.svg is larger than 15 MB; an SVG image is read whole to be checked, so it can have at most 15 MB.` });
  expect(await mcp.sh(`cat "${path}"`)).toEqual({ text: `cat: ${path}: No such file or directory`, isError: true });
});

test("svg: an SVG image isn't moved into a skill's folder @svg", async ({ mcp, folder }) => {
  const id = folder.slice(-6);
  const path = `/Work/${folder}/icon ${id}.svg`;
  const target = `/Work/_skills/e2e-${id}/icon.svg`;
  await write(mcp, path, drawing(id));
  expect(await mcp.sh(`mv "${path}" "${target}"`)).toEqual({
    text: `mv: ${target}: icon ${id}.svg is an SVG image, and inside a skill's folder an .svg is a text file; upload it there instead`,
    isError: true,
  });
  // Nothing moved: it stays where it was, and no skill's folder came into being.
  expect((await mcp.sh(`cat "${path}"`)).text.split("\n")[0]).toBe(`icon ${id}.svg: image (SVG), 120×60, ${Buffer.byteLength(drawing(id))} B`);
  expect(await mcp.sh(`ls /Work/_skills/e2e-${id}`)).toEqual({ text: `ls: cannot access '/Work/_skills/e2e-${id}': No such file or directory`, isError: true });
});

test("svg: a script is kept, and /api/files serves it under the sandbox policy @svg", async ({ mcp, folder, aliceApi }) => {
  const id = folder.slice(-6);
  const path = `/Work/${folder}/scripted ${id}.svg`;
  const source = drawing(id, 40, 40, `<script>document.title = "ran"</script>`);
  await write(mcp, path, source);
  expect((await mcp.sh(`cat "${path}"`)).text.split("\n")[1]).toBe(source.trimEnd());

  for (const query of ["", "?download=1"]) {
    const file = await aliceApi.send("GET", `/api/files/${encodePath(path)}${query}`);
    expect(file.status()).toBe(200);
    const headers = file.headers();
    expect({
      type: headers["content-type"],
      policy: headers["content-security-policy"],
      sniff: headers["x-content-type-options"],
    }).toEqual({ type: "image/svg+xml", policy: sandbox, sniff: "nosniff" });
    expect(await file.text()).toBe(source);
  }
});

test.describe("svg published", () => {
  let alice: Api;
  /** Alice's agent with "May publish" in Work. */
  let key: Mcp;

  test.beforeAll(async () => {
    alice = await Api.as("alice");
    key = await Mcp.create(alice, `e2e svg ${randomBytes(3).toString("hex")}`, { Work: "write" }, ["Work"]);
  });

  test.afterAll(async () => {
    await key?.dispose();
    await alice?.dispose();
  });

  test("svg: a published SVG keeps its drawing without an editor's names, under the sandbox policy @svg", async ({ folder }) => {
    const id = folder.slice(-6);
    const svg = `/Work/${folder}/sketch ${id}.svg`;
    const note = `/Work/${folder}/Sketch ${id}.md`;
    // An editor's file: a comment, metadata and Inkscape's own names, which tell who drew it and where it lay.
    const source =
      `<?xml version="1.0"?>\n<!-- drawn by e2e-author-${id} -->\n` +
      `<svg xmlns="http://www.w3.org/2000/svg" xmlns:sodipodi="http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd" ` +
      `xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape" width="40" height="40" sodipodi:docname="e2e-docname-${id}.svg" ` +
      `inkscape:export-filename="/home/e2e-home-${id}/sketch.png"><metadata>e2e-metadata-${id}</metadata>` +
      `<sodipodi:namedview id="e2e-view-${id}"/><rect id="e2e-${id}" width="40" height="40" fill="#2a6"/>` +
      // A reference within the file stays; a linked image on the author's disk and XHTML in a foreignObject go.
      `<use href="#e2e-${id}" x="5"/><image href="file:///home/e2e-link-${id}/x.png" width="4" height="4"/>` +
      `<foreignObject width="10" height="10"><div xmlns="http://www.w3.org/1999/xhtml">e2e-xhtml-${id}</div></foreignObject></svg>\n`;
    written.push(svg, note);
    for (const [path, content] of [
      [svg, source],
      [note, `# Sketch\n\n![[sketch ${id}.svg]]\n`],
    ]) {
      const answer = await key.call("write", { path, content });
      expect(answer.isError, answer.text).toBe(false);
    }

    const anon = await request.newContext({ baseURL, extraHTTPHeaders: { Origin: baseURL } });
    try {
      const added = await key.sh(`publish add "${note}"`);
      expect(added.isError, `publish add failed:\n${redact(added.text)}`).toBe(false);
      const link = /^link \(shown once\): (\S+)$/m.exec(added.text)?.[1];
      expect(link !== undefined, `publish add printed a link:\n${redact(added.text)}`).toBe(true);

      await reserveReaderRead();
      // Guarded: a call log would show the code in the body.
      const read = await guarded(() => anon.post("/api/s", { data: { code: new URL(link!).hash.slice(1) } }), "POST /api/s");
      expect(read.status(), "the reader's read").toBe(200);
      const view = (await read.json()) as { markdown: string; copy: string; files: { index: number; mediaType: string }[] };
      expect(view.files).toEqual([{ index: 0, mediaType: "image/svg+xml" }]);
      expect(view.markdown).toContain("](f/0)");

      await reserveReaderRead();
      const file = await guarded(() => anon.get("/api/s/files/0", { headers: { "X-Kumbu-Copy": view.copy } }), "GET /api/s/files/0");
      expect(file.status(), "the reader's file").toBe(200);
      expect({ type: file.headers()["content-type"], policy: file.headers()["content-security-policy"], sniff: file.headers()["x-content-type-options"] }).toEqual({
        type: "image/svg+xml",
        policy: sandbox,
        sniff: "nosniff",
      });
      const copied = await file.text();
      expect(copied, "the drawing stays").toContain(`<rect id="e2e-${id}" width="40" height="40" fill="#2a6"`);
      expect(copied, "a reference within the file stays").toContain(`href="#e2e-${id}"`);
      expect(
        [`e2e-author-${id}`, `e2e-docname-${id}`, `e2e-home-${id}`, `e2e-metadata-${id}`, `e2e-view-${id}`, `e2e-link-${id}`, `e2e-xhtml-${id}`, "file:", "sodipodi", "inkscape"].filter((t) => copied.includes(t)),
        "an editor's names in the published copy",
      ).toEqual([]);
    } finally {
      await anon.dispose();
      await key.sh(`publish rm "${note}"`);
    }
    expect((await key.sh(`publish ls /Work/${folder}`)).text).toBe(`no published notes below /Work/${folder}`);
  });
});

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
