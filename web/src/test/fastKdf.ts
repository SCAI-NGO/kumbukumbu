// SPDX-License-Identifier: AGPL-3.0-or-later
// A password KDF for tests: SHA-256 over the parameters, salt and password, so a copy's wrap opens in no time. Never
// the app's (offline/kdf.ts, Argon2id).
import type { PasswordKdf } from "eidolon-core/store/crypto";

export const fastKdf: PasswordKdf = {
  async derive(password, salt, params) {
    const label = new TextEncoder().encode(`${JSON.stringify(params)}\0${Array.from(salt).join(",")}\0${password}`);
    return new Uint8Array(await crypto.subtle.digest("SHA-256", label));
  },
};
