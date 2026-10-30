// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { HttpError } from "./client";
import { errorText } from "./error-text";

describe("errorText", () => {
  it("passes on the server's own sentence for a refusal", () => {
    expect(errorText(new HttpError(401, "The username or password is wrong."))).toBe("The username or password is wrong.");
  });

  it("says the server can't be reached for faults and network failures", () => {
    const unreachable = "The server can't be reached right now. Try again in a moment.";
    expect(errorText(new HttpError(502, ""))).toBe(unreachable);
    expect(errorText(new TypeError("Failed to fetch"))).toBe(unreachable);
  });

  it("passes on the sentence of a vault being re-encrypted, and not of any other 503", () => {
    expect(errorText(new HttpError(503, "This vault is being re-encrypted; try again in a moment."))).toBe(
      "This vault is being re-encrypted; try again in a moment.",
    );
    expect(errorText(new HttpError(503, ""))).toBe("The server can't be reached right now. Try again in a moment.");
  });

  it("answers a password-required 401 with a sentence of its own", () => {
    const sentence = "This needs your password, so nothing was done.";
    expect(errorText(new HttpError(401, "This needs your password again: confirm it with POST /api/session/confirm.", "password-required"))).toBe(sentence);
    expect(errorText(new HttpError(401, "Sign in first."))).toBe("Sign in first.");
  });

  it("falls back to a plain sentence", () => {
    expect(errorText(new Error("boom"))).toBe("Something went wrong. Try again.");
  });
});
