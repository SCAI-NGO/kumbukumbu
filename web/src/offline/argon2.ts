// SPDX-License-Identifier: AGPL-3.0-or-later
// Argon2id for the offline copy's password wrap (design §4.2): hash-wasm's WebAssembly build, and @noble/hashes'
// plain script where WebAssembly is refused. Both are Argon2id (RFC 9106): the same password, salt and parameters
// give the same bytes, so the second is only slower, never another key. Runs in the KDF worker (kdfWorker.ts), the
// one script allowed to compile WebAssembly (the server's KdfWorkerPolicy).
import type { KdfParams } from "eidolon-core/store/crypto";

type Bytes = Uint8Array<ArrayBuffer>;

export async function argon2id(password: string, salt: Bytes, k: KdfParams): Promise<Bytes> {
  try {
    const { argon2id: wasm } = await import("hash-wasm");
    return new Uint8Array(await wasm({
      password, salt, parallelism: k.p, iterations: k.t, memorySize: k.m, hashLength: 32, outputType: "binary",
    }));
  } catch {
    // WebAssembly refused (a browser that holds the worker to the page's policy) or missing.
    const { argon2idAsync } = await import("@noble/hashes/argon2.js");
    return new Uint8Array(await argon2idAsync(password, salt, { t: k.t, m: k.m, p: k.p, dkLen: 32 }));
  }
}

