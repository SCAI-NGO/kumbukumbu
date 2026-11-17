// SPDX-License-Identifier: AGPL-3.0-or-later
import { Outlet, useLocation } from "react-router-dom";
import WindowFrame from "../../windows/WindowFrame";
import WindowNav, { type NavEntry } from "../../windows/WindowNav";
import AdminOnly from "./AdminOnly";

const entries: NavEntry[] = [
  { to: "/admin/users", label: "Users", match: "/admin/users" },
  { to: "/admin/invitations", label: "Invitations", match: "/admin/invitations" },
];

/** The admin pages, a page per entry, each with its own address. */
export default function AdminWindow() {
  const { pathname } = useLocation();
  return (
    <WindowFrame kind="navigated" title="Admin" heading={entries.find((e) => pathname.startsWith(e.match))?.label} nav={<WindowNav label="Admin" entries={entries} />}>
      <div className="mx-auto flex w-full max-w-xl flex-col gap-6 p-4">
        <AdminOnly>
          <Outlet />
        </AdminOnly>
      </div>
    </WindowFrame>
  );
}
