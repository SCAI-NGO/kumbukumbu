// SPDX-License-Identifier: AGPL-3.0-or-later
import { argon2idAsync } from "@noble/hashes/argon2.js";
import { argon2id as wasm } from "hash-wasm";
import { describe, expect, test, vi } from "vitest";
import { argon2id } from "./argon2";

const hex = (b: Uint8Array) => Buffer.from(b).toString("hex");
const small = { name: "argon2id" as const, m: 64, t: 2, p: 1 };

describe("Argon2id for the password wrap", () => {
  test("the plain-script stand-in is Argon2id: RFC 9106's test vector", async () => {
    const tag = await argon2idAsync(new Uint8Array(32).fill(1), new Uint8Array(16).fill(2), {
      t: 3, m: 32, p: 4, dkLen: 32, key: new Uint8Array(8).fill(3), personalization: new Uint8Array(12).fill(4),
    });
    expect(hex(tag)).toBe("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659");
  });

  test("WebAssembly and the stand-in give the same key, so either opens what the other wrapped", async () => {
    const salt = new Uint8Array(16).fill(7);
    const fromWasm = await argon2id("correct horse", salt, small);
    const fromScript = await argon2idAsync("correct horse", salt, { t: 2, m: 64, p: 1, dkLen: 32 });
    expect(fromWasm).toHaveLength(32);
    expect(hex(fromWasm)).toBe(hex(fromScript));
    expect(hex(await wasm({ password: "correct horse", salt, parallelism: 1, iterations: 2, memorySize: 64, hashLength: 32, outputType: "binary" }))).toBe(hex(fromWasm));
  });

  test("without WebAssembly it still answers, by the stand-in", async () => {
    const salt = new Uint8Array(16).fill(9);
    const expected = hex(await argon2id("pw", salt, small));
    vi.doMock("hash-wasm", () => ({ argon2id: () => Promise.reject(new WebAssembly.CompileError("refused by the page's policy")) }));
    vi.resetModules();
    const { argon2id: refused } = await import("./argon2");
    expect(hex(await refused("pw", salt, small))).toBe(expected);
    vi.doUnmock("hash-wasm");
  });
});
