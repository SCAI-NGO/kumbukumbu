// SPDX-License-Identifier: AGPL-3.0-or-later
import createClient, { type Middleware } from "openapi-fetch";
import { useOfflineCopy } from "../offline/copy";
import { lockCopy, useOfflineMode, wipeCopy } from "../offline/mode";
import { endPendingSignOut, signOutPending } from "../offline/unlock";
import { usePasswordPrompt } from "../state/passwordPrompt";
import { useSession } from "../state/session";
import { useUpdate } from "../update/store";
import type { paths } from "./schema";

/**
 * A refusal or fault from the API; `message` is the server's sentence for the user, if it sent one, `type` its
 * machine-readable kind, and `code` a write refusal's reason (collab design §3.5): see `RefusalCode`.
 */
export class HttpError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly type?: string,
    readonly code?: string,
  ) {
    super(message);
  }
}

/** Why the server refused a write: the level is below `write`, below `maintain` for the vault's structure, the vault is closing, or its owner is deactivated. */
export type RefusalCode = "read-only" | "structural" | "closing" | "owner-inactive";
const refusalCodes: readonly string[] = ["read-only", "structural", "closing", "owner-inactive"];

export const isRefusal = (error: unknown): error is HttpError & { code: RefusalCode } =>
  error instanceof HttpError && error.code !== undefined && refusalCodes.includes(error.code);

/**
 * Told of every refused write: the caller's level changed since the vault list was read (a role lowered, the owner
 * deactivated), so the shell reads it again and the write actions follow.
 */
const refusalListeners = new Set<(error: HttpError) => void>();
export function onRefusal(listener: (error: HttpError) => void) {
  refusalListeners.add(listener);
  return () => void refusalListeners.delete(listener);
}

// These answer 401 for wrong credentials, not for a missing session; the screens show that themselves.
const anonymous = new Set(["POST /api/session", "POST /api/join", "POST /api/recover", "POST /api/invitations/peek", "POST /api/pairing/redeem"]);

/** A remembered session whose password window has closed: a guarded action wants the password again, the session lives on. */
export async function isPasswordRequired(response: Response): Promise<boolean> {
  if (response.status !== 401) return false;
  const body: unknown = await response.clone().json().catch(() => null);
  return !!body && typeof body === "object" && "type" in body && body.type === "password-required";
}

/** A 401 for a missing session ends it. `key`: "PUT /api/files/{vault}/{path}". */
async function endOnLostSession(key: string, response: Response) {
  if (response.status !== 401 || anonymous.has(key) || (await isPasswordRequired(response))) return;
  const body: unknown = await response.clone().json().catch(() => null);
  // The request handed in the offline copy's slot, which is gone: the copy goes too (offline mode design §4.6).
  if (!!body && typeof body === "object" && "code" in body && body.code === "mirror-wipe") return void (await wipeCopy());
  // Back from offline with changes waiting: the bar asks to sign in again over the app, which keeps the place (§3a).
  const mode = useOfflineMode.getState();
  if (mode.offline && mode.waiting > 0) return mode.sessionLost();
  // After a sign-out, the page being left may still ask (or hear back) for a moment; those 401s are the sign-out
  // itself. Asking for the session is the way back in, and still leads to sign-in.
  if (useSession.getState().signedOut && key !== "GET /api/session") return;
  // The copy's life in the tab follows the session's: no way back past the sign-in page without the password, and
  // the next user never inherits it.
  void lockCopy();
  useSession.getState().end();
}

const sessionEnds: Middleware = {
  onResponse: ({ request, response, schemaPath }) => endOnLostSession(`${request.method} ${schemaPath}`, response),
};

/** Gateways answer these while Kumbu² behind them is down. */
const unreachable = new Set([502, 503, 504]);

/**
 * What the offline mode needs to hear of every request (offline mode design §3a): whether it reached Kumbu²; and the
 * offline copy's slot token goes with it, so a 401 can say the copy was removed (§4.6).
 */
const reachWatch: Middleware = {
  onRequest({ request }) {
    const token = useOfflineCopy.getState().header?.slot.token;
    if (token !== undefined) request.headers.set("Kumbu-Mirror", token);
    return request;
  },
  onResponse({ response }) {
    if (unreachable.has(response.status)) return void useOfflineMode.getState().failed();
    useOfflineMode.getState().reached();
    // Kumbu² answers again: a sign-out made while away reaches it now, not only at the next start.
    if (signOutPending()) void endPendingSignOut();
  },
  onError({ error }) {
    // fetch fails with a TypeError when it gets no answer; a cancelled request (an AbortError) says nothing.
    if (error instanceof TypeError) useOfflineMode.getState().failed();
  },
};

// Every API answer carries the server's build version: a different one means a release to reload into.
const versionWatch: Middleware = {
  onResponse({ response }) {
    useUpdate.getState().report(response.headers.get("X-Kumbu-Version"));
  },
};

/** What the middleware hears of every answer, for a request sent without openapi-fetch (an upload with progress). */
export async function heard(key: string, response: Response) {
  if (unreachable.has(response.status)) useOfflineMode.getState().failed();
  else useOfflineMode.getState().reached();
  useUpdate.getState().report(response.headers.get("X-Kumbu-Version"));
  await endOnLostSession(key, response);
}

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
export function pathSerializer(pathname: string, params: Record<string, unknown>) {
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
api.use(reachWatch);
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
    const code = error && typeof error === "object" && "code" in error && typeof error.code === "string" ? error.code : undefined;
    const refused = new HttpError(response.status, message, type, code);
    if (isRefusal(refused)) for (const listener of refusalListeners) listener(refused);
    throw refused;
  }
  return data as T;
}
