// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { api, call } from "../../api/client";

/** Whether the signed-in user is an admin, from the cached session; `undefined` while it loads. */
export function useIsAdmin() {
  const session = useQuery({ queryKey: ["session"], queryFn: () => call(api.GET("/api/session")) });
  const role = session.data?.user.role;
  return session.data ? role === "admin" || role === "primary_admin" : undefined;
}

/** The admin pages' guard. The server refuses everyone else anyway; this says so kindly instead of showing an error. */
export default function AdminOnly({ children }: { children: ReactNode }) {
  // Same query as the session guard, so this is answered from the cache.
  const isAdmin = useIsAdmin();
  if (isAdmin === undefined) return null;
  return isAdmin ? children : <p className="text-text-body">Only an admin can see this.</p>;
}
