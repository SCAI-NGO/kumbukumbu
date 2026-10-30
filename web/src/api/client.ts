// SPDX-License-Identifier: AGPL-3.0-or-later
import createClient, { type Middleware } from "openapi-fetch";
import { usePasswordPrompt } from "../state/passwordPrompt";
import { useSession } from "../state/session";
import { useUpdate } from "../update/store";
import type { paths } from "./schema";

/** A refusal or fault from the API; `message` is the server's sentence for the user, if it sent one, `type` its machine-readable kind. */
export class HttpError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly type?: string,
  ) {
    super(message);
  }
}

// These answer 401 for wrong credentials, not for a missing session; the screens show that themselves.
const anonymous = new Set(["POST /api/session", "POST /api/join", "POST /api/recover", "POST /api/invitations/peek", "POST /api/pairing/redeem"]);

/** A remembered session whose password window has closed: a guarded action wants the password again, the session lives on. */
export async function isPasswordRequired(response: Response): Promise<boolean> {
  if (response.status !== 401) return false;
  const body: unknown = await response.clone().json().catch(() => null);
  return !!body && typeof body === "object" && "type" in body && body.type === "password-required";
}

const sessionEnds: Middleware = {
  async onResponse({ request, response, schemaPath }) {
    const key = `${request.method} ${schemaPath}`;
    if (response.status !== 401 || anonymous.has(key) || (await isPasswordRequired(response))) return;
    // After a sign-out, the page being left may still ask (or hear back) for a moment; those 401s are the sign-out
    // itself. Asking for the session is the way back in, and still leads to sign-in.
    if (useSession.getState().signedOut && key !== "GET /api/session") return;
    useSession.getState().end();
  },
};

// Every API answer carries the server's build version: a different one means a release to reload into.
const versionWatch: Middleware = {
  onResponse({ response }) {
    useUpdate.getState().report(response.headers.get("X-Kumbu-Version"));
  },
};

// What a guarded request sent, by openapi-fetch's request id, to send again once the password is confirmed. Reads are
// never guarded, so only the others are kept, and of those only the ones without a body or with a JSON one: every
// guarded action sends JSON, while an upload's bytes (a file, a form) would be copied for nothing, as no upload is guarded.
const sent = new Map<string, Request>();

const repeatable = (request: Request) =>
  request.method !== "GET" && (request.body === null || (request.headers.get("Content-Type") ?? "").startsWith("application/json"));

/**
 * Asks for the password when a guarded action finds the window closed, and repeats the request once after it is
 * confirmed; the repeated answer goes back as it is. Cancelled, the action fails with the original answer.
 */
const passwordWindow: Middleware = {
  onRequest({ request, id }) {
    if (repeatable(request)) sent.set(id, request.clone());
  },
  async onResponse({ response, id }) {
    const clone = sent.get(id);
    sent.delete(id);
    if (!clone || !(await isPasswordRequired(response))) return;
    if (await usePasswordPrompt.getState().ask()) return globalThis.fetch(clone);
  },
  onError({ id }) {
    sent.delete(id);
  },
};

/** The `{**…}` parameters of the API: a path within a vault. */
const multiSegment = new Set(["path", "folder"]);

/**
 * `{path}` and `{folder}` (a path in its vault) keep their slashes, each segment encoded; any other value is one
 * segment. An empty `{folder}` (the vault's root) leaves its slash out too: `/api/upload/Work`.
 */
function pathSerializer(pathname: string, params: Record<string, unknown>) {
  return pathname.replace(/\/?\{(\w+)\}/g, (match, name: string) => {
    const value = String(params[name]);
    const slash = match.startsWith("/") ? "/" : "";
    if (!multiSegment.has(name)) return slash + encodeURIComponent(value);
    return value === "" ? "" : slash + value.split("/").map(encodeURIComponent).join("/");
  });
}

export const api = createClient<paths>({
  // Absolute, as Request wants outside the browser too; same origin either way.
  baseUrl: window.location.origin,
  credentials: "include",
  pathSerializer,
  // Looked up per call, so tests can stub it.
  fetch: (request) => globalThis.fetch(request),
});
api.use(sessionEnds);
api.use(versionWatch);
// After the two above: openapi-fetch hands answers to the last one first, so they see the repeated answer.
api.use(passwordWindow);

/** The data of an openapi-fetch call, or an HttpError with the server's `{ error }` sentence. */
export async function call<T>(request: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await request;
  if (!response.ok) {
    const message = error && typeof error === "object" && "error" in error && typeof error.error === "string" ? error.error : "";
    const type = error && typeof error === "object" && "type" in error && typeof error.type === "string" ? error.type : undefined;
    throw new HttpError(response.status, message, type);
  }
  return data as T;
}
