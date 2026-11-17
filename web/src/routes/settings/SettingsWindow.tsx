// SPDX-License-Identifier: AGPL-3.0-or-later
import { Outlet, useLocation } from "react-router-dom";
import WindowFrame from "../../windows/WindowFrame";
import WindowNav, { type NavEntry } from "../../windows/WindowNav";

/** The maintainer's order (windows design, "Settings"): Kumbu² is built around agents, so Access stands high, Skills close by. */
const entries: NavEntry[] = [
  { to: "/settings/appearance", label: "Appearance", match: "/settings/appearance" },
  { to: "/settings/vaults", label: "Vaults", match: "/settings/vaults" },
  { to: "/settings/access/keys", label: "Access", match: "/settings/access" },
  { to: "/settings/skills/installed", label: "Skills", match: "/settings/skills" },
  { to: "/settings/calendar", label: "Calendar", match: "/settings/calendar" },
  { to: "/settings/security", label: "Security", match: "/settings/security" },
  { to: "/settings/data", label: "Your data", match: "/settings/data" },
  { to: "/settings/about", label: "About", match: "/settings/about" },
];

/** The settings, a page per entry, each with its own address. */
export default function SettingsWindow() {
  const { pathname } = useLocation();
  return (
    <WindowFrame kind="navigated" title="Settings" heading={entries.find((e) => pathname.startsWith(e.match))?.label} nav={<WindowNav label="Settings" entries={entries} />}>
      <div className="mx-auto flex w-full max-w-xl flex-col gap-6 p-4">
        <Outlet />
      </div>
    </WindowFrame>
  );
}
