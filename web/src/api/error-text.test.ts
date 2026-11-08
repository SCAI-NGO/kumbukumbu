// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { HttpError } from "./client";
import { errorText } from "./error-text";
import { rememberStorage } from "./storage";
import { useSession } from "../state/session";

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

  it("quota code gets the owner's sentence", () => {
    const GB = 1024 ** 3;
    const refusal = new HttpError(507, "This vault is out of space.", undefined, "quota");
    // Before her storage was asked, the sentence has no size.
    expect(errorText(refusal)).toBe("Your storage is full. Empty the trash or ask your admin for more.");
    useSession.getState().signedIn({ user: { id: "u1", username: "ada", display: "Ada", role: "user" }, previousSignInAt: null });
    try {
      rememberStorage("u1", { used: 2 * GB, limit: 2 * GB, archive: 0 });
      expect(errorText(refusal)).toBe("Your storage is full (2 GB). Empty the trash or ask your admin for more.");
      // A member is told the sentence alone, with no number.
      expect(errorText(new HttpError(507, "This vault is out of space."))).toBe("This vault is out of space.");
      // Another user signed in since: her figure isn't this one's.
      useSession.getState().signedIn({ user: { id: "u2", username: "bob", display: "Bob", role: "user" }, previousSignInAt: null });
      expect(errorText(refusal)).toBe("Your storage is full. Empty the trash or ask your admin for more.");
    } finally {
      useSession.getState().reset();
    }
  });

  it("falls back to a plain sentence", () => {
    expect(errorText(new Error("boom"))).toBe("Something went wrong. Try again.");
  });
});
