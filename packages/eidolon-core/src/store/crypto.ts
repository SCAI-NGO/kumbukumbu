// SPDX-License-Identifier: AGPL-3.0-or-later
// The sealed mirror's cryptography (offline mode design §4.1, §4.2). The host's WebCrypto comes in as a port, in the
// shape of the few calls used here, so the core sees no DOM: a browser passes `crypto.subtle`, Node its webcrypto's.
//
// One mirror key (AES-256, made by the server, §4.6) per user and browser. Two keys are derived from it with HKDF and
// imported non-extractable, so script can use them but never read them: one seals records (AES-GCM, a fresh nonce
// per record, the record's storage key as additional data, so a record moved to another key no longer opens), one
// names them (HMAC-SHA-256 of the vault id and the path, so storage keys say nothing of either).

/** A CryptoKey: opaque here. */
export type Key = object;

type Bytes = Uint8Array<ArrayBuffer>;
type Usage = "encrypt" | "decrypt" | "sign" | "deriveKey";

export interface Subtle {
  importKey(format: "raw", keyData: Bytes, algorithm: "HKDF" | "AES-GCM", extractable: false, usages: Usage[]): Promise<Key>;
  deriveKey(
    algorithm: { name: "HKDF"; hash: "SHA-256"; salt: Bytes; info: Bytes },
    baseKey: Key, derivedKeyType: { name: "AES-GCM"; length: 256 } | { name: "HMAC"; hash: "SHA-256"; length: 256 },
    extractable: false, usages: Usage[]): Promise<Key>;
  encrypt(algorithm: { name: "AES-GCM"; iv: Bytes; additionalData: Bytes }, key: Key, data: Bytes): Promise<ArrayBuffer>;
  decrypt(algorithm: { name: "AES-GCM"; iv: Bytes; additionalData: Bytes }, key: Key, data: Bytes): Promise<ArrayBuffer>;
  sign(algorithm: "HMAC", key: Key, data: Bytes): Promise<ArrayBuffer>;
}

export interface CryptoPort {
  readonly subtle: Subtle;
  /** Bytes from the host's secure random source (`crypto.getRandomValues`). */
  randomBytes(length: number): Bytes;
  /** The password hash: the host brings it, as WebCrypto has none that is memory-hard (design §4.2). */
  readonly kdf: PasswordKdf;
}

/** Argon2id's parameters as RFC 9106 names them: memory in KiB, passes, lanes. */
export interface KdfParams { name: "argon2id"; m: number; t: number; p: number; }

/**
 * 64 MiB and 3 passes, RFC 9106's second recommendation, with one lane: WebAssembly runs on one thread, so more lanes
 * only cost time. The header keeps them, so later copies can take more without breaking earlier ones.
 */
export const KDF_PARAMS: KdfParams = { name: "argon2id", m: 65_536, t: 3, p: 1 };

/** Argon2id, by the host's implementation: 32 bytes from the password, NFC already, and the salt. */
export interface PasswordKdf {
  derive(password: string, salt: Bytes, params: KdfParams): Promise<Bytes>;
}

/**
 * Parameters a header may carry: a header changed on the device mustn't make the next unlock take all memory or
 * hours. Weaker ones than ours open as well; they would only come from a wrap made with them, which needs the key.
 */
export function validKdf(k: unknown): k is KdfParams {
  if (k === null || typeof k !== "object") return false;
  const { name, m, t, p } = k as Record<string, unknown>;
  const int = (v: unknown, min: number, max: number) => typeof v === "number" && Number.isInteger(v) && v >= min && v <= max;
  return name === "argon2id" && int(p, 1, 8) && int(m, 8 * (p as number), 1_048_576) && int(t, 1, 16);
}

const NONCE_BYTES = 12;
const encoder = new TextEncoder();

export interface MirrorKeys { readonly seal: Key; readonly name: Key; }

/** The record keys from the raw mirror key. The caller wipes `raw` afterwards; the keys can't be read back. */
export async function deriveKeys(c: CryptoPort, raw: Bytes): Promise<MirrorKeys> {
  const base = await c.subtle.importKey("raw", raw, "HKDF", false, ["deriveKey"]);
  const hkdf = (info: string) => ({ name: "HKDF" as const, hash: "SHA-256" as const, salt: new Uint8Array(0), info: encoder.encode(info) });
  const [seal, name] = await Promise.all([
    c.subtle.deriveKey(hkdf("kumbu-mirror records"), base, { name: "AES-GCM", length: 256 }, false, ["encrypt", "decrypt"]),
    c.subtle.deriveKey(hkdf("kumbu-mirror names"), base, { name: "HMAC", hash: "SHA-256", length: 256 }, false, ["sign"]),
  ]);
  return { seal, name };
}

/** nonce ‖ AES-GCM(plain), bound to `aad`. */
export async function seal(c: CryptoPort, key: Key, plain: Bytes, aad: string): Promise<Bytes> {
  const iv = c.randomBytes(NONCE_BYTES);
  const sealed = new Uint8Array(await c.subtle.encrypt({ name: "AES-GCM", iv, additionalData: encoder.encode(aad) }, key, plain));
  const out = new Uint8Array(NONCE_BYTES + sealed.byteLength);
  out.set(iv);
  out.set(sealed, NONCE_BYTES);
  return out;
}

/** The plaintext, or null when the record doesn't open: another key, another `aad`, or changed bytes. */
export async function open(c: CryptoPort, key: Key, sealed: Bytes, aad: string): Promise<Bytes | null> {
  if (sealed.byteLength < NONCE_BYTES) return null;
  try {
    const iv = sealed.slice(0, NONCE_BYTES);
    return new Uint8Array(await c.subtle.decrypt({ name: "AES-GCM", iv, additionalData: encoder.encode(aad) }, key, sealed.slice(NONCE_BYTES)));
  } catch {
    return null;
  }
}

/** A record's name: HMAC of `label`, Base64Url. */
export async function nameOf(c: CryptoPort, key: Key, label: string): Promise<string> {
  return toBase64Url(new Uint8Array(await c.subtle.sign("HMAC", key, encoder.encode(label))));
}

/** The mirror key sealed under a key from the password, for unlocking offline (design §4.2). Base64Url throughout. */
export interface PasswordWrap { kdf: KdfParams; salt: string; wrapped: string; }

async function passwordKey(c: CryptoPort, password: string, salt: Bytes, params: KdfParams): Promise<Key> {
  const bytes = await c.kdf.derive(password.normalize("NFC"), salt, params);
  if (bytes.byteLength !== 32) throw new Error("The password hash gave a key of the wrong size.");
  try {
    return await c.subtle.importKey("raw", bytes, "AES-GCM", false, ["encrypt", "decrypt"]);
  } finally {
    bytes.fill(0);
  }
}

/** `aad` binds the wrap to its copy, so a wrap copied into another copy's header doesn't open. A fresh salt each time. */
export async function wrapWithPassword(c: CryptoPort, raw: Bytes, password: string, aad: string, params = KDF_PARAMS): Promise<PasswordWrap> {
  const salt = c.randomBytes(16);
  const key = await passwordKey(c, password, salt, params);
  return { kdf: params, salt: toBase64Url(salt), wrapped: toBase64Url(await seal(c, key, raw, aad)) };
}

/** The raw mirror key, or null for a wrong password, or a wrap this version can't open. */
export async function unwrapWithPassword(c: CryptoPort, wrap: PasswordWrap, password: string, aad: string): Promise<Bytes | null> {
  const salt = fromBase64Url(wrap.salt);
  const wrapped = fromBase64Url(wrap.wrapped);
  if (salt === null || wrapped === null || salt.byteLength !== 16 || !validKdf(wrap.kdf)) return null;
  return open(c, await passwordKey(c, password, salt, wrap.kdf), wrapped, aad);
}

const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

export function toBase64Url(bytes: Uint8Array): string {
  let out = "";
  for (let i = 0; i < bytes.length; i += 3) {
    const n = (bytes[i]! << 16) | ((bytes[i + 1] ?? 0) << 8) | (bytes[i + 2] ?? 0);
    out += ALPHABET[n >> 18]! + ALPHABET[(n >> 12) & 63]!;
    if (i + 1 < bytes.length) out += ALPHABET[(n >> 6) & 63]!;
    if (i + 2 < bytes.length) out += ALPHABET[n & 63]!;
  }
  return out;
}

/** Null for anything but unpadded Base64Url, so a bad header or server answer can't be half read. */
export function fromBase64Url(text: string): Bytes | null {
  if (text.length % 4 === 1) return null;
  const out = new Uint8Array(Math.floor((text.length * 3) / 4));
  let bits = 0;
  let value = 0;
  let at = 0;
  for (const ch of text) {
    const n = ALPHABET.indexOf(ch);
    if (n < 0) return null;
    value = (value << 6) | n;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out[at++] = (value >> bits) & 255;
    }
  }
  return out;
}
