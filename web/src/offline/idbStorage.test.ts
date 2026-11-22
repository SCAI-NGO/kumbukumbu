// SPDX-License-Identifier: AGPL-3.0-or-later
import "fake-indexeddb/auto";
import { IDBFactory } from "fake-indexeddb";
import { describe, expect, test } from "vitest";
import { openRecordStorage } from "./idbStorage";

const b = (...n: number[]) => new Uint8Array(n);

describe("the records in IndexedDB", () => {
  test("are kept, found by prefix in key order, and removed by key and by prefix in one transaction", async () => {
    const storage = await openRecordStorage("test", new IDBFactory());
    await storage.write({ put: [["c/x/v/a/e/2", b(2)], ["c/x/v/a/e/1", b(1)], ["c/x/v/b/e/1", b(3)], ["h/x", b(9)]] });
    expect(await storage.get("h/x")).toEqual(b(9));
    expect(await storage.get("nothing")).toBeNull();
    expect(await storage.scan("c/x/v/a/")).toEqual([["c/x/v/a/e/1", b(1)], ["c/x/v/a/e/2", b(2)]]);

    await storage.write({ removePrefix: ["c/x/v/a/"], remove: ["h/x"], put: [["c/x/m/b", b(4)]] });
    expect((await storage.scan("")).map(([k]) => k)).toEqual(["c/x/m/b", "c/x/v/b/e/1"]);
  });

  test("a put keeps its own bytes, whatever the caller does with its array afterwards", async () => {
    const storage = await openRecordStorage("test", new IDBFactory());
    const value = b(1, 2, 3);
    await storage.write({ put: [["k", value]] });
    value[0] = 7;
    expect(await storage.get("k")).toEqual(b(1, 2, 3));
  });

  test("a failed transaction writes nothing", async () => {
    const storage = await openRecordStorage("test", new IDBFactory());
    await storage.write({ put: [["k", b(1)]] });
    // An invalid key aborts the transaction after the first put went in.
    await expect(storage.write({ put: [["k", b(2)], [undefined as unknown as string, b(3)]] })).rejects.toThrow();
    expect(await storage.get("k")).toEqual(b(1));
  });
});
