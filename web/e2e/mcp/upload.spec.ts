// SPDX-License-Identifier: AGPL-3.0-or-later
// Binary upload for agents through MCP (spec 2026-11-17-binary-upload-for-agents-design.md, PR #230; spec of this run
// §3 and the brief's open point 2). The stack runs as Production on plain HTTP, and there the upload URL is spent and
// refused with 403 before its ticket is looked up (src/Kumbu.Server/Auth/CallerMiddleware.cs:321-329): so the suite
// tests the tool's answer, its refusals and that 403, never with a loosened rule (no Development, no trusted proxy, no
// forged X-Forwarded-Proto, no TLS added). The URL and the curl line are secrets: they go through `redact` before any
// `expect` that would print them, and the URL is asserted by its parts.
//
// Promises in scope (spec → test):
//  the tool's answer: the URL <origin>/api/agent-upload/<token>, the origin
//  KUMBU_PUBLIC_URL's; when it expires (UTC, 10 minutes) and that it works
//  once; the curl line; the limits and that the format is checked from the
//  bytes                                                                    → "upload: the answer names the URL …"
//  a token of 32 random bytes, URL-safe (Base64url: 43 characters), a new
//  one per call                                                             → the same test
//  refusals before a ticket exists: a note's path, an existing file without
//  a version, a wrong version, a version for a free path                    → "upload: refuses a note's path …"
//  a read-only key refused at the tool, not later                           → "upload: refuses a read-only key …"
//  HTTPS only outside Development: over plain HTTP 403 "Uploads need HTTPS;
//  this URL was sent in the clear and is spent.", with the sentence to ask
//  again, and nothing stored; the same answer for a token that doesn't
//  exist                                                                    → "upload: a PUT over plain HTTP …"
// Left out (spec §3; brief, open point 2):
//  single use (a second PUT 401), expiry, a revoked key or connection, the
//  per-credential cap, the stored file as the agent's write, the answer's
//  markdown and link, 400/409/413/507: the PUT never gets past the HTTPS
//  refusal here, and that refusal comes before the ticket is looked up, so a
//  second PUT proves nothing about spending. Covered by UploadTicketsTests,
//  AgentUploadApiTests and the hand test from a claude.ai sandbox (spec §6).
//  The size limit: the spec says 15 MB; the files spec (2026-11-18, decision
//  11) raised the bound per file to the admin's setting, 1 GiB by default,
//  and the answer names that bound: asserted as 1 GiB, the stack's default.
//  The kumbu-binary-upload skill and the README sections: the skill is listed
//  in the handshake (@memory reads the instructions), the README is no
//  promise an MCP test can check.
import { request } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { encodePath, guarded, Mcp } from "../support/api";
import { baseURL } from "../support/env";
import { expect, redact, test as base } from "./fixtures";

const test = base.extend<{ put: (path: string, content: string | Buffer) => Promise<string> }>({
  // Writes a note (`.md`) or a file and removes it at the test's end, whatever happened; every path is tried.
  put: async ({ aliceApi }, use) => {
    const written: string[] = [];
    await use(async (path, content) => {
      written.push(path);
      if (path.endsWith(".md")) await aliceApi.putNote(path, content.toString());
      else await aliceApi.putFile(path, typeof content === "string" ? Buffer.from(content, "utf8") : content);
      return path;
    });
    const failures: string[] = [];
    for (const path of written.reverse()) {
      const response = await aliceApi.send("DELETE", `/api/${path.endsWith(".md") ? "notes" : "files"}/${encodePath(path)}`);
      if (!response.ok() && response.status() !== 404) failures.push(`DELETE ${path}: ${response.status()}`);
    }
    if (failures.length) throw new Error(failures.join("; "));
  },
});

/** A 1 × 1 PNG, so a format's bytes are at hand (the PUT never reads them here). */
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", "base64");

const refusalOverHttp = "Uploads need HTTPS; this URL was sent in the clear and is spent. Ask for a new upload URL with the upload tool.";

/** The tool's answer taken apart: the URL (a secret: never into an expect as it is), the expiry and the curl line. */
function parse(text: string) {
  const [url, expires, curl] = text.split("\n");
  return { url, expires: expires.replace(/^expires /, ""), curl };
}

/** A PUT of `bytes` to an upload URL with no credential, as curl sends it; guarded, so a failure doesn't print the URL. */
async function putTo(url: string, bytes: Buffer) {
  const context = await request.newContext();
  try {
    const response = await guarded(() => context.put(url, { data: bytes, headers: { "Content-Type": "application/octet-stream" } }), "PUT the upload URL");
    return { status: response.status(), body: (await response.json()) as { error: string } };
  } finally {
    await context.dispose();
  }
}

test("upload: the answer names the URL, its expiry, the curl line and the limits @upload", async ({ mcp, folder }) => {
  const dir = `/Work/${folder}`;
  const before = Date.now();
  const image = await mcp.call("upload", { path: `${dir}/diagram.png` });
  const file = await mcp.call("upload", { path: `${dir}/report.zip` });
  const after = Date.now();
  expect(image.isError, redact(image.text)).toBe(false);
  expect(file.isError, redact(file.text)).toBe(false);

  const answers = [
    { answer: image, limits: "at most 1 GiB, and its content must be what the name diagram.png says (the format is checked from the bytes)" },
    // A file of no format Kumbu² knows: nothing to check in its bytes.
    { answer: file, limits: "at most 1 GiB" },
  ];
  for (const { answer, limits } of answers) {
    const { url, expires, curl } = parse(answer.text);
    // The whole answer, with the URL blanked: the method, the body, once, the expiry and what to do on failure.
    expect(redact(answer.text)).toBe(
      [
        `${baseURL}/api/agent-upload/<redacted>`,
        `expires ${expires}`,
        `curl -sS -X PUT --data-binary @<local file> '${baseURL}/api/agent-upload/<redacted>'`,
        "",
        `Run that curl line in a shell on the machine that holds the file, with <local file> its path. The URL works once and until the time above; ${limits}. If the PUT fails, call upload again for a new URL.`,
      ].join("\n"),
    );

    // The URL by its parts: KUMBU_PUBLIC_URL's origin (env.sh), the route, a token of 32 bytes in Base64url.
    const parsed = new URL(url);
    expect(parsed.origin).toBe(baseURL);
    expect(parsed.search + parsed.hash).toBe("");
    const token = parsed.pathname.replace(/^\/api\/agent-upload\//, "");
    expect(parsed.pathname.startsWith("/api/agent-upload/"), "the URL's path").toBe(true);
    expect(token.length, "the token's length").toBe(43);
    expect(/^[A-Za-z0-9_-]+$/.test(token), "the token is URL-safe").toBe(true);
    expect(curl === `curl -sS -X PUT --data-binary @<local file> '${url}'`, "the curl line PUTs to the URL above").toBe(true);

    // Ten minutes from the call, in UTC, to the second.
    expect(expires).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/);
    const at = Date.parse(expires);
    expect(at, "expires 10 minutes after the call").toBeGreaterThanOrEqual(Math.floor((before + 10 * 60_000) / 1000) * 1000);
    expect(at, "expires 10 minutes after the call").toBeLessThanOrEqual(after + 10 * 60_000);
  }
  expect(parse(image.text).url !== parse(file.text).url, "each call has a URL of its own").toBe(true);

  // Spent here, as the stack is on plain HTTP: no ticket stays open for the rest of the run.
  for (const { answer } of answers) expect((await putTo(parse(answer.text).url, png)).status).toBe(403);
});

test("upload: refuses a note's path, an existing file without its version, a wrong or needless version @upload", async ({ mcp, put, folder }) => {
  const dir = `/Work/${folder}`;
  await put(`${dir}/Plan.md`, "# Plan\n");
  await put(`${dir}/data.txt`, "hello");
  const refusals: [Record<string, string>, string][] = [
    [{ path: `${dir}/Plan.md` }, `upload: ${dir}/Plan.md is a note; create or replace it with the write tool`],
    [{ path: `${dir}/data.txt` }, `upload: ${dir}/data.txt exists already; to replace it, pass its version (sh: cat shows it)`],
    // data.txt's version is the start of its bytes' SHA-256, 2cf24dba5fb0 for "hello".
    [{ path: `${dir}/data.txt`, version: "0123456789ab" }, `upload: ${dir}/data.txt has changed since version 0123456789ab (it is now 2cf24dba5fb0)`],
    [{ path: `${dir}/new.png`, version: "0123456789ab" }, `upload: ${dir}/new.png doesn't exist, so it has no version; leave version out to create it`],
  ];
  for (const [args, refusal] of refusals) {
    const answer = await mcp.call("upload", args);
    expect({ text: redact(answer.text), isError: answer.isError }, JSON.stringify(args)).toEqual({ text: refusal, isError: true });
  }
  // The refusals changed nothing: the note and the file are as written, the free path still free.
  expect(await mcp.sh(`cat ${dir}/data.txt`)).toMatchObject({ isError: false });
  expect((await mcp.sh(`cat ${dir}/data.txt`)).text).toContain("hello");
  expect(await mcp.sh(`ls ${dir}`)).toEqual({ text: "data.txt  text 5 B\nPlan.md", isError: false });
});

test("upload: refuses a read-only key at the tool, before a URL exists @upload", async ({ aliceApi, folder }) => {
  test.fail(true, "M-02 (Low, bug): the refusal of a read-only key says 'upload: ' twice");
  const reader = await Mcp.create(aliceApi, `e2e upload reader ${randomBytes(3).toString("hex")}`, { Work: "read" });
  try {
    const answer = await reader.call("upload", { path: `/Work/${folder}/diagram.png` });
    expect(answer.isError, "refused").toBe(true);
    expect(answer.text.includes("/api/agent-upload/"), "no URL is handed out").toBe(false);
    // The read-only sentence every write tool gives a key (ICaller.ReadOnlySentence), after the tool's name once.
    expect(redact(answer.text), "M-02: the refusal names the tool once").toBe("upload: this key can only read vault Work");
  } finally {
    await reader.dispose();
  }
});

test("upload: a PUT over plain HTTP is refused with 403 and stores nothing @upload", async ({ mcp, folder }) => {
  const path = `/Work/${folder}/diagram.png`;
  const answer = await mcp.call("upload", { path });
  expect(answer.isError, redact(answer.text)).toBe(false);
  const { url } = parse(answer.text);
  expect(new URL(url).protocol, "the stack's URL is plain HTTP").toBe("http:");

  // CallerMiddleware.cs:323-329: spent and refused before the ticket is looked up; the sentence to ask again follows.
  expect(await putTo(url, png)).toEqual({ status: 403, body: { error: refusalOverHttp } });
  expect(await mcp.sh(`ls ${path}`)).toEqual({ text: `ls: cannot access '${path}': No such file or directory`, isError: true });

  // A token that was never handed out gets the same answer: a guess over HTTP learns nothing.
  const guess = `${baseURL}/api/agent-upload/${randomBytes(32).toString("base64url")}`;
  expect(await putTo(guess, png)).toEqual({ status: 403, body: { error: refusalOverHttp } });
});
