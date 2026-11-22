// SPDX-License-Identifier: AGPL-3.0-or-later
import { expect, test } from "vitest";
import { decide, type Action, type Sides } from "../src/engine/decide";

const v = (x: string) => x.padEnd(12, "0");
type Row = [string, Omit<Sides, "remoteDamagedPossible">, Action];
const rows: Row[] = [
  ["same/same",                 { local: v("a"), remote: v("a"), base: v("a") }, { kind: "nothing" }],
  ["local changed",             { local: v("b"), remote: v("a"), base: v("a") }, { kind: "upload", base: v("a") }],
  ["remote changed",            { local: v("a"), remote: v("b"), base: v("a") }, { kind: "download" }],
  ["remote gone, local same",   { local: v("a"), remote: null,   base: v("a") }, { kind: "trashLocal" }],
  ["both changed, equal",       { local: v("c"), remote: v("c"), base: v("a") }, { kind: "recordBase", version: v("c") }],
  ["both changed",              { local: v("b"), remote: v("c"), base: v("a") }, { kind: "conflict" }],
  ["deleted locally",           { local: null,   remote: v("a"), base: v("a") }, { kind: "deleteRemote", base: v("a") }],
  ["deleted locally, remote changed", { local: null, remote: v("c"), base: v("a") }, { kind: "download" }],
  ["local changed, remote gone",{ local: v("b"), remote: null,   base: v("a") }, { kind: "reupload" }],
  ["new locally",               { local: v("b"), remote: null,   base: null   }, { kind: "upload", base: null }],
  ["new remotely",              { local: null,   remote: v("b"), base: null   }, { kind: "download" }],
  ["both new, equal",           { local: v("b"), remote: v("b"), base: null   }, { kind: "recordBase", version: v("b") }],
  ["both new, different",       { local: v("b"), remote: v("c"), base: null   }, { kind: "conflict" }],
  ["both gone",                 { local: null,   remote: null,   base: v("a") }, { kind: "nothing" }],
];
test.each(rows)("%s", (_, sides, action) => expect(decide({ ...sides, remoteDamagedPossible: false })).toEqual(action));

test("absence is not a deletion while the vault has damaged notes", () => {
  expect(decide({ local: v("a"), remote: null, base: v("a"), remoteDamagedPossible: true })).toEqual({ kind: "nothing" });
});
test("a local edit is still re-uploaded while the vault has damaged notes", () => {
  // The damaged rule only stops trashing; an edit is never held back by it.
  expect(decide({ local: v("b"), remote: null, base: v("a"), remoteDamagedPossible: true })).toEqual({ kind: "reupload" });
});
