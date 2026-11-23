// SPDX-License-Identifier: AGPL-3.0-or-later
// The KDF worker: Argon2id off the page's thread, and the one script the server lets compile WebAssembly, by a policy
// of its own on this file (KdfWorkerPolicy). It takes a password, a salt and parameters, and answers 32 bytes.
import type { KdfParams } from "eidolon-core/store/crypto";
import { argon2id } from "./argon2";

interface Ask { id: number; password: string; salt: Uint8Array<ArrayBuffer>; params: KdfParams }

self.onmessage = (event: MessageEvent<Ask>) => {
  const { id, password, salt, params } = event.data;
  argon2id(password, salt, params).then(
    (key) => self.postMessage({ id, key }, { transfer: [key.buffer] }),
    (e: unknown) => self.postMessage({ id, error: e instanceof Error ? e.message : String(e) }),
  );
};
