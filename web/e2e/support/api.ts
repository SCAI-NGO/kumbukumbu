// SPDX-License-Identifier: AGPL-3.0-or-later
import { expect, request, type APIRequestContext, type APIResponse } from "@playwright/test";
import { readFileSync } from "node:fs";
import { baseURL, secrets, storageState, type User } from "./env";
import { addDays, today } from "./time";

/** `/Work/Pump/Plan.md` → `Work/Pump/Plan.md` with each segment encoded, as the web app's client sends it. */
export const encodePath = (path: string) => path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/");
const vaultOf = (path: string) => path.replace(/^\//, "").split("/")[0];
const inVault = (path: string) => path.replace(/^\//, "").split("/").slice(1).join("/");

async function ok(response: APIResponse, what: string) {
  if (!response.ok()) throw new Error(`${what}: ${response.status()} ${await response.text()}`);
  return response;
}

export type Note = { path: string; content: string; version: string };

/**
 * The web app's API as a signed-in user calls it: the saved session's cookie, and the Origin every state-changing
 * request needs (Security.cs). For the setup and for steps a test doesn't drive through the screens.
 */
export class Api {
  private constructor(
    readonly context: APIRequestContext,
    readonly user: User | "anonymous",
  ) {}

  static async as(user: User, state: string | undefined = storageState(user)) {
    return new Api(await request.newContext({ baseURL, storageState: state, extraHTTPHeaders: { Origin: baseURL } }), user);
  }

  static async from(context: APIRequestContext, user: User) {
    return new Api(context, user);
  }

  static async anonymous() {
    return new Api(await request.newContext({ baseURL, extraHTTPHeaders: { Origin: baseURL } }), "anonymous");
  }

  async dispose() {
    await this.context.dispose();
  }

  async json<T>(method: "GET" | "POST" | "PUT" | "DELETE", url: string, data?: unknown): Promise<T> {
    const response = await ok(await this.context.fetch(url, { method, data }), `${method} ${url}`);
    const text = await response.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  async getNote(path: string): Promise<Note> {
    return this.json("GET", `/api/notes/${encodePath(path)}`);
  }

  /** Writes a note, creating it, or replacing it at its current version. */
  async putNote(path: string, content: string): Promise<Note> {
    const current = await this.context.get(`/api/notes/${encodePath(path)}`);
    const version = current.ok() ? ((await current.json()) as Note).version : undefined;
    return this.json("PUT", `/api/notes/${encodePath(path)}`, { content, ...(version ? { version } : {}) });
  }

  /** Uploads an image's or PDF's bytes, as the web app's upload does (PUT /api/files). */
  async putFile(path: string, bytes: Buffer) {
    const response = await this.context.put(`/api/files/${encodePath(path)}`, {
      data: bytes,
      headers: { "Content-Type": "application/octet-stream" },
    });
    return ok(response, `PUT file ${path}`);
  }

  async deleteNote(path: string) {
    await ok(await this.context.delete(`/api/notes/${encodePath(path)}`), `DELETE ${path}`);
  }

  async history(path: string): Promise<unknown[]> {
    return this.json("GET", `/api/history/${encodePath(path)}`);
  }

  /** The web console (read-only): `sh` in this session, as /console runs it. */
  async console(command: string): Promise<{ text: string; isError: boolean }> {
    const view = await this.json<{ parts: { type: string; text: string | null }[]; isError: boolean }>("POST", "/api/console", { call: "sh", command });
    return { text: view.parts.map((p) => p.text ?? "").join(""), isError: view.isError };
  }

  /** Confirms the password, opening the window guarded actions need. */
  async confirmPassword() {
    if (this.user === "anonymous") throw new Error("No password for anonymous.");
    await this.json("POST", "/api/session/confirm", { password: secrets().passwords[this.user] });
  }

  static vaultOf = vaultOf;
  static inVault = inVault;
}

/**
 * Dates relative to today in Vienna, in fixture notes: `{{today}}`, `{{today+30}}`, `{{today-400}}` (days) become
 * YYYY-MM-DD when the note is written.
 */
export const withDates = (text: string) =>
  text.replace(/\{\{today(?:([+-])(\d+))?\}\}/g, (_, sign: string | undefined, days: string | undefined) =>
    addDays(today(), sign ? (sign === "-" ? -1 : 1) * Number(days) : 0),
  );

/** Writes each fixture file below `to`: Markdown as notes (with `withDates`), the rest (images, PDFs) as files. */
export async function seed(api: Api, files: { source: string; path: string }[]) {
  for (const { source, path } of files) {
    const bytes = readFileSync(source);
    if (path.endsWith(".md")) await api.putNote(path, withDates(bytes.toString("utf8")));
    else await api.putFile(path, bytes);
  }
}

/**
 * An agent's `sh`, `write` or `board` through MCP with an API key of Alice's (Streamable HTTP, stateless), for the
 * writing steps the read-only console refuses. The key lives in memory for one worker.
 */
export class Mcp {
  private id = 0;
  constructor(
    private readonly context: APIRequestContext,
    private readonly key: string,
  ) {}

  static async create(api: Api, name: string, vaults: Record<string, "read" | "write" | "maintain">) {
    await api.confirmPassword();
    const created = await api.json<{ key: string }>("POST", "/api/keys", { name, vaults, expiresInDays: 30 });
    return new Mcp(await request.newContext({ baseURL }), created.key);
  }

  async call(tool: string, args: Record<string, unknown>): Promise<{ text: string; isError: boolean }> {
    const response = await this.context.post("/mcp", {
      headers: { Authorization: `Bearer ${this.key}`, Accept: "application/json, text/event-stream", "Content-Type": "application/json" },
      data: { jsonrpc: "2.0", id: ++this.id, method: "tools/call", params: { name: tool, arguments: args } },
    });
    expect(response.status(), `MCP ${tool}`).toBe(200);
    const body = await response.text();
    // Either a JSON answer or one server-sent event carrying it.
    const json = body.trimStart().startsWith("{") ? body : (body.split("\n").find((l) => l.startsWith("data:")) ?? "").slice(5);
    const message = JSON.parse(json) as { result?: { content: { type: string; text: string }[]; isError?: boolean }; error?: { message: string } };
    if (message.error) throw new Error(`MCP ${tool}: ${message.error.message}`);
    return { text: message.result!.content.map((c) => c.text).join("\n"), isError: !!message.result!.isError };
  }

  sh(command: string) {
    return this.call("sh", { command });
  }

  async dispose() {
    await this.context.dispose();
  }
}
