// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { NavLink } from "react-router-dom";
import { api, call } from "../../api/client";
import { AccountScreen } from "../../components/form";

const tab = ({ isActive }: { isActive: boolean }) =>
  `flex min-h-11 items-center rounded-[10px] px-4 ${isActive ? "bg-accent text-on-accent" : "border border-line bg-surface-2 text-text"}`;

/** The frame of the admin pages. The server refuses everyone else anyway; this says so kindly instead of showing an error. */
export default function AdminOnly({ title, children }: { title: string; children: ReactNode }) {
  // Same query as the session guard, so this is answered from the cache.
  const session = useQuery({ queryKey: ["session"], queryFn: () => call(api.GET("/api/session")) });
  const role = session.data?.user.role;

  if (role !== "admin" && role !== "primary_admin") {
    return (
      <AccountScreen title={title}>
        {session.data && <p className="text-text-body">Only an admin can see this.</p>}
      </AccountScreen>
    );
  }

  return (
    <AccountScreen title={title}>
      <nav aria-label="Admin" className="flex gap-2">
        <NavLink to="/admin/users" className={tab}>
          Users
        </NavLink>
        <NavLink to="/admin/invitations" className={tab}>
          Invitations
        </NavLink>
      </nav>
      {children}
    </AccountScreen>
  );
}

