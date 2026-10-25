// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from chatsundere (packages/ui-shared/src/login/login-error-copy.ts)
import { HttpError } from "./client";

/**
 * The sentence to show for a failed call. The API's refusals already carry a sentence meant for the user, so
 * only faults, a lost connection and anything unexpected get one of their own.
 */
export function errorText(err: unknown): string {
  if (err instanceof HttpError) {
    if (err.status >= 500) return "The server can't be reached right now. Try again in a moment.";
    if (err.message) return err.message;
  }
  // fetch rejects with a TypeError when the network or the server is gone.
  if (err instanceof TypeError) return "The server can't be reached right now. Try again in a moment.";
  return "Something went wrong. Try again.";
}
