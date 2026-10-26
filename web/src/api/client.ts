// SPDX-License-Identifier: AGPL-3.0-or-later
import createClient, { type Middleware } from "openapi-fetch";
import { useSession } from "../state/session";
import { useUpdate } from "../update/store";
import type { paths } from "./schema";

/** A refusal or fault from the API; `message` is the server's sentence for the user, if it sent one. */
export class HttpError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

// These answer 401 for wrong credentials, not for a missing session; the screens show that themselves.
const anonymous = new Set(["POST /api/session", "POST /api/join", "POST /api/recover", "POST /api/invitations/peek", "POST /api/pairing/redeem"]);

const sessionEnds: Middleware = {
  onResponse({ request, response, schemaPath }) {
    const key = `${request.method} ${schemaPath}`;
    if (response.status !== 401 || anonymous.has(key)) return;
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

/** The data of an openapi-fetch call, or an HttpError with the server's `{ error }` sentence. */
export async function call<T>(request: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await request;
  if (!response.ok) {
    const message = error && typeof error === "object" && "error" in error && typeof error.error === "string" ? error.error : "";
    throw new HttpError(response.status, message);
  }
  return data as T;
}
