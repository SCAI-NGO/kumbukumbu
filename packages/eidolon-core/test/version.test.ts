// SPDX-License-Identifier: AGPL-3.0-or-later
import { expect, test } from "vitest";
import { byteSize, MARKER_VERSION, MAX_BYTES, normaliseText, versionOf, versionOfBytes } from "../src/engine/version";

test("version matches the server's token", async () => {
  // printf '# Plan\n' | sha256sum → c3964bb3b70a…
  expect(await versionOf("# Plan\n")).toBe("c3964bb3b70a");
  expect(await versionOf("# Plan\r\n")).toBe("c3964bb3b70a");
});
test("byte size counts UTF-8 after CRLF → LF", () => {
  expect(byteSize("ä\r\n")).toBe(3);
});
test("only CRLF is normalised", () => {
  expect(normaliseText("a\r\nb\rc\n")).toBe("a\nb\rc\n");
});
test("the size limit is 1 MiB", () => {
  expect(MAX_BYTES).toBe(1_048_576);
});
test("versionOfBytes does not normalise CRLF", async () => {
  // An attachment's token is of its raw bytes (the server's VersionToken.Of over bytes): a CR is a byte like any other.
  // printf '# Plan\r\n' | sha256sum → 5b8518e2ef9f…
  const bytes = new TextEncoder().encode("# Plan\r\n");
  expect(await versionOfBytes(bytes)).toBe("5b8518e2ef9f");
  expect(await versionOfBytes(bytes)).not.toBe(await versionOf("# Plan\r\n"));
  expect(await versionOfBytes(bytes.slice().buffer)).toBe(await versionOfBytes(bytes));
  expect(await versionOfBytes(new TextEncoder().encode("# Plan\n"))).toBe("c3964bb3b70a");
});
test("the marker's version is the token of zero bytes", async () => {
  expect(await versionOfBytes(new Uint8Array(0))).toBe(MARKER_VERSION);
  expect(MARKER_VERSION).toBe("e3b0c44298fc");
});
