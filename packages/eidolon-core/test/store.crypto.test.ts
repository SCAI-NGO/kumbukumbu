// SPDX-License-Identifier: AGPL-3.0-or-later
// The sealed store's cryptography (offline mode design §4.2).
import { describe, expect, test } from "vitest";
import { deriveKeys, fromBase64Url, nameOf, open, seal, toBase64Url, unwrapWithPassword, wrapWithPassword } from "../src/store/crypto";
import { nodeCrypto } from "./fakeStore";

const c = nodeCrypto;
const bytes = (text: string) => new TextEncoder().encode(text);
const text = (b: Uint8Array | null) => (b === null ? null : new TextDecoder().decode(b));

describe("Base64Url", () => {
  test("round-trips every length, as the server's encoder writes it", () => {
    for (let n = 0; n < 40; n++) {
      const b = c.randomBytes(n);
      const encoded = toBase64Url(b);
      expect(encoded).toBe(Buffer.from(b).toString("base64url"));
      expect(fromBase64Url(encoded)).toEqual(b);
    }
  });

  test("refuses anything else", () => {
    expect(fromBase64Url("abc+")).toBeNull();
    expect(fromBase64Url("abc=")).toBeNull();
    expect(fromBase64Url("a")).toBeNull();
  });
});

describe("records", () => {
  test("open only under their key, at their place, unchanged", async () => {
    const keys = await deriveKeys(c, c.randomBytes(32));
    const other = await deriveKeys(c, c.randomBytes(32));
    const sealed = await seal(c, keys.seal, bytes("Dear diary"), "c/x/v/a/b/1");
    expect(text(await open(c, keys.seal, sealed, "c/x/v/a/b/1"))).toBe("Dear diary");
    expect(await open(c, other.seal, sealed, "c/x/v/a/b/1")).toBeNull();
    expect(await open(c, keys.seal, sealed, "c/x/v/a/b/2")).toBeNull();
    const flipped = sealed.slice();
    flipped[20] = flipped[20]! ^ 1;
    expect(await open(c, keys.seal, flipped, "c/x/v/a/b/1")).toBeNull();
  });

  test("each seal takes a fresh nonce", async () => {
    const keys = await deriveKeys(c, c.randomBytes(32));
    const a = await seal(c, keys.seal, bytes("same"), "k");
    const b = await seal(c, keys.seal, bytes("same"), "k");
    expect(toBase64Url(a)).not.toBe(toBase64Url(b));
  });

  test("names are the same for the same label under one key, and differ under another", async () => {
    const raw = c.randomBytes(32);
    const keys = await deriveKeys(c, raw.slice());
    const again = await deriveKeys(c, raw.slice());
    const other = await deriveKeys(c, c.randomBytes(32));
    expect(await nameOf(c, keys.name, "path\0v1\0Diary.md")).toBe(await nameOf(c, again.name, "path\0v1\0Diary.md"));
    expect(await nameOf(c, keys.name, "path\0v1\0Diary.md")).not.toBe(await nameOf(c, other.name, "path\0v1\0Diary.md"));
    expect(await nameOf(c, keys.name, "path\0v1\0Diary.md")).not.toContain("Diary");
  });

  test("the derived keys can't be read back", async () => {
    const keys = await deriveKeys(c, c.randomBytes(32));
    await expect(webcryptoExport(keys.seal)).rejects.toThrow();
    await expect(webcryptoExport(keys.name)).rejects.toThrow();
  });
});

describe("the password wrap", () => {
  test("opens with the password and its copy only", async () => {
    const raw = c.randomBytes(32);
    const wrap = await wrapWithPassword(c, raw, "correct horse", "wrap\0copy1", 1000);
    expect(wrap.iterations).toBe(1000);
    expect(await unwrapWithPassword(c, wrap, "correct horse", "wrap\0copy1")).toEqual(raw);
    expect(await unwrapWithPassword(c, wrap, "correct hors", "wrap\0copy1")).toBeNull();
    expect(await unwrapWithPassword(c, wrap, "correct horse", "wrap\0copy2")).toBeNull();
  });

  test("the password is taken in NFC, as a password manager or another keyboard may type it", async () => {
    const raw = c.randomBytes(32);
    const wrap = await wrapWithPassword(c, raw, "Kaffeé", "a", 1000);
    expect(await unwrapWithPassword(c, wrap, "Kaffeé", "a")).toEqual(raw);
  });

  test("takes a fresh salt each time", async () => {
    const raw = c.randomBytes(32);
    const a = await wrapWithPassword(c, raw, "pw", "a", 1000);
    const b = await wrapWithPassword(c, raw, "pw", "a", 1000);
    expect(a.salt).not.toBe(b.salt);
  });
});

function webcryptoExport(key: object): Promise<ArrayBuffer> {
  return globalThis.crypto.subtle.exportKey("raw", key as CryptoKey);
}
