// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { aiPrompt } from "./copyForAi";

describe("aiPrompt", () => {
  it("builds the paragraph an AI can act on", () => {
    expect(aiPrompt("Ideas", "before-cleanup", "2026-10-19T12:02:00Z", "/Ideas/concepts/xyz.md")).toBe(
      'From my Kumbu² vault Ideas, checkpoint before-cleanup (19 Oct 2026): /Ideas/concepts/xyz.md. Read it with: cat --checkpoint "before-cleanup" "/Ideas/concepts/xyz.md"',
    );
  });

  it("escapes quotes and backslashes", () => {
    expect(aiPrompt("Ideas", "before-cleanup", "2026-10-19T12:02:00Z", '/Ideas/a "b" \\c.md').endsWith('"/Ideas/a \\"b\\" \\\\c.md"')).toBe(true);
  });
});
