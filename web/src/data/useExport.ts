// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { download, withTicket } from "./exportUrl";

/**
 * An export, which needs the password again: it is checked for a ticket that the download then carries (the
 * download is a plain GET, and a password never goes into a URL). `start` resolves true once the download began.
 */
export function useExport() {
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function start(href: string, password: string): Promise<boolean> {
    setPending(true);
    setError(null);
    try {
      const { ticket } = await call(api.POST("/api/export/ticket", { body: { password } }));
      download(withTicket(href, ticket));
      return true;
    } catch (e) {
      setError(errorText(e));
      return false;
    } finally {
      setPending(false);
    }
  }

  return { start, pending, error };
}
