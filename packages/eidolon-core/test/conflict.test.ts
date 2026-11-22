// SPDX-License-Identifier: AGPL-3.0-or-later
import { expect, test } from "vitest";
import { conflictCopyText, writerIdentity } from "../src/engine/conflict";

const o = {
  device: "Laptop", localAt: new Date(2026, 9, 27, 10, 15), remoteWriter: "Work – Obsidian Android-7Q",
  remoteAt: new Date(2026, 9, 27, 10, 14), originalPath: "projects/Plan.md",
};
const callout = [
  "> [!warning] Kumbu² conflict",
  "> Change from **Laptop** at 2026-10-27 10:15.",
  "> The change from **Work – Obsidian Android-7Q** at 10:14 is in [[projects/Plan|Plan]].",
  "> Merge what you need, then delete this copy.",
  "",
  "",
].join("\n");

test("the callout comes first in plain text", () => {
  expect(conflictCopyText("# Plan\nbody\n", o)).toBe(callout + "# Plan\nbody\n");
});
test("in a shared vault the callout says this device", () => {
  const text = conflictCopyText("# Plan\n", { ...o, device: null });
  expect(text).toContain("> Change from **this device** at 2026-10-27 10:15.");
  expect(text).not.toContain("Laptop");
  expect(text.endsWith("# Plan\n")).toBe(true);
});
test("in a shared vault the callout names no writer either", () => {
  expect(conflictCopyText("# Plan\n", { ...o, device: null })).toBe([
    "> [!warning] Kumbu² conflict",
    "> Change from **this device** at 2026-10-27 10:15.",
    "> The other change, at 10:14, is in [[projects/Plan|Plan]].",
    "> Merge what you need, then delete this copy.",
    "",
    "",
  ].join("\n") + "# Plan\n");
});
test("the callout goes after frontmatter, which stays byte-identical", () => {
  const fm = "---\ntags: [a]\ntitle: x\n---\n";
  expect(conflictCopyText(fm + "body\n", o)).toBe(fm + callout + "body\n");
});
test("frontmatter with CRLF is kept as it is", () => {
  const fm = "---\r\ntags: [a]\r\n---\r\n";
  expect(conflictCopyText(fm + "body", o)).toBe(fm + callout + "body");
});
test("text starting with --- but never closing it has no frontmatter", () => {
  expect(conflictCopyText("---\nnot closed\n", o)).toBe(callout + "---\nnot closed\n");
});
test("the writer is cut at the first ' ('", () => {
  expect(writerIdentity("Work – Obsidian Android-7Q (edit)")).toBe("Work – Obsidian Android-7Q");
  expect(conflictCopyText("x", { ...o, remoteWriter: "Work – Obsidian Android-7Q (edit)" }))
    .toContain("> The change from **Work – Obsidian Android-7Q** at 10:14");
});
test("the link is path-qualified, with the title", () => {
  expect(conflictCopyText("x", { ...o, originalPath: "Plan.md" })).toContain("[[Plan|Plan]]");
  expect(conflictCopyText("x", o)).toContain("[[projects/Plan|Plan]]");
});
test("frontmatter closing at the end of the text gets a line break before the callout", () => {
  expect(conflictCopyText("---\na: 1\n---", o)).toBe("---\na: 1\n---\n" + callout);
});
test("empty frontmatter counts", () => {
  expect(conflictCopyText("---\n---\nbody", o)).toBe("---\n---\n" + callout + "body");
});
