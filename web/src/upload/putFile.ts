// SPDX-License-Identifier: AGPL-3.0-or-later
import { api, HttpError } from "../api/client";
import type { components } from "../api/schema";

type FileState = components["schemas"]["FileStateView"];

/** A PUT's outcome: written (`created`: the path was free), or a 409 with the file as it is now. */
export type PutResult = { kind: "created" | "replaced"; state: FileState } | { kind: "conflict"; current: FileState };

/**
 * Uploads an image's or PDF's bytes to `path` in the vault (`PUT /api/files/{vault}/{path}`). Without `version`
 * only a free path is written; with it, only the file at that version is replaced. Any other refusal (a format or
 * size problem, 400 or 413, with the server's sentence) throws an `HttpError`.
 */
export async function putFile(vault: string, path: string, bytes: Uint8Array, version?: string): Promise<PutResult> {
  const { data, error, response } = await api.PUT("/api/files/{vault}/{path}", {
    params: { path: { vault, path }, query: version === undefined ? undefined : { version } },
    // The bytes as they are: openapi-fetch would send JSON otherwise.
    body: bytes as unknown as string,
    bodySerializer: (body: unknown) => body as BodyInit,
    headers: { "Content-Type": "application/octet-stream" },
  });
  if (data) return { kind: response.status === 201 ? "created" : "replaced", state: data };
  if (response.status === 409 && error && "current" in error) return { kind: "conflict", current: error.current };
  const message = error && typeof error === "object" && "error" in error && typeof error.error === "string" ? error.error : "";
  throw new HttpError(response.status, message);
}
