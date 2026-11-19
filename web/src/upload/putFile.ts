// SPDX-License-Identifier: AGPL-3.0-or-later
import { api, heard, HttpError, pathSerializer } from "../api/client";
import type { components } from "../api/schema";

type FileState = components["schemas"]["FileStateView"];

/** A PUT's outcome: written (`created`: the path was free), or a 409 with the file as it is now. */
export type PutResult = { kind: "created" | "replaced"; state: FileState } | { kind: "conflict"; current: FileState };

const PUT_FILE = "/api/files/{vault}/{path}";

/**
 * Uploads a file's bytes to `path` in the vault (`PUT /api/files/{vault}/{path}`). Without `version` only a free path
 * is written; with it, only the file at that version is replaced. Any other refusal throws an `HttpError` with the
 * server's sentence: a format or size problem (400, 413), or a 409 without a file state (the path is a folder, or a
 * note sits below it). A `Blob` (a file over 15 MB) goes as it is, streamed from the disk, with `onProgress` told the
 * share sent; bytes go through openapi-fetch.
 */
export async function putFile(vault: string, path: string, body: Uint8Array | Blob, version?: string, onProgress?: (sent: number) => void): Promise<PutResult> {
  if (body instanceof Blob) return outcome(await putWithProgress(vault, path, body, version, onProgress));
  const { data, error, response } = await api.PUT(PUT_FILE, {
    params: { path: { vault, path }, query: version === undefined ? undefined : { version } },
    // The bytes as they are: openapi-fetch would send JSON otherwise.
    body: body as unknown as string,
    bodySerializer: (bytes: unknown) => bytes as BodyInit,
    headers: { "Content-Type": "application/octet-stream" },
  });
  if (data) return { kind: response.status === 201 ? "created" : "replaced", state: data };
  return refusal(response.status, error);
}

function refusal(status: number, error: unknown): PutResult {
  if (status === 409 && error && typeof error === "object" && "current" in error) return { kind: "conflict", current: error.current as FileState };
  const message = error && typeof error === "object" && "error" in error && typeof error.error === "string" ? error.error : "";
  throw new HttpError(status, message);
}

async function outcome(response: Response): Promise<PutResult> {
  await heard(`PUT ${PUT_FILE}`, response);
  const body: unknown = await response.json().catch(() => null);
  if (response.ok) return { kind: response.status === 201 ? "created" : "replaced", state: body as FileState };
  return refusal(response.status, body);
}

/**
 * The PUT by XMLHttpRequest, the one way a browser reports how much of a body it has sent (fetch has no upload
 * progress). Same origin, so the session's cookie goes along as with fetch. A network failure rejects with a
 * TypeError, as fetch's does, so it reads as the server out of reach.
 */
function putWithProgress(vault: string, path: string, file: Blob, version: string | undefined, onProgress?: (sent: number) => void) {
  const url = new URL(pathSerializer(PUT_FILE, { vault, path }), window.location.origin);
  if (version !== undefined) url.searchParams.set("version", version);
  return new Promise<Response>((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open("PUT", url);
    request.setRequestHeader("Content-Type", "application/octet-stream");
    request.upload.onprogress = (e) => {
      if (e.lengthComputable && e.total > 0) onProgress?.(e.loaded / e.total);
    };
    request.onload = () => {
      const headers = new Headers();
      for (const line of request.getAllResponseHeaders().trim().split(/[\r\n]+/)) {
        const colon = line.indexOf(":");
        if (colon > 0) headers.append(line.slice(0, colon).trim(), line.slice(colon + 1).trim());
      }
      resolve(new Response(request.responseText || null, { status: request.status, headers }));
    };
    request.onerror = () => reject(new TypeError("The upload failed"));
    request.onabort = () => reject(new TypeError("The upload was cut off"));
    request.send(file);
  });
}
