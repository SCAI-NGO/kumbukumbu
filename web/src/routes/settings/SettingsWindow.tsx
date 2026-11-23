// SPDX-License-Identifier: AGPL-3.0-or-later
import { Outlet, useLocation } from "react-router-dom";
import { useByok } from "../../api/notes";
import WindowFrame from "../../windows/WindowFrame";
import WindowNav, { type NavEntry } from "../../windows/WindowNav";

/** The maintainer's order (windows design, "Settings"): Kumbu² is built around agents, so Access stands high, Skills close by. */
const entries: NavEntry[] = [
  { to: "/settings/appearance", label: "Appearance", match: "/settings/appearance" },
  { to: "/settings/vaults", label: "Vaults", match: "/settings/vaults" },
  // Beside Vaults, where its list was before it got a page of its own.
  { to: "/settings/published", label: "Published", match: "/settings/published" },
  { to: "/settings/access/keys", label: "Access", match: "/settings/access" },
  { to: "/settings/skills/installed", label: "Skills", match: "/settings/skills" },
  { to: "/settings/calendar", label: "Calendar", match: "/settings/calendar" },
  { to: "/settings/external", label: "External", match: "/settings/external" },
  { to: "/settings/security", label: "Security", match: "/settings/security" },
  { to: "/settings/data", label: "Your data", match: "/settings/data" },
  { to: "/settings/about", label: "About", match: "/settings/about" },
];

/** The settings, a page per entry, each with its own address. */
export default function SettingsWindow() {
  const { pathname } = useLocation();
  // External holds the services this server lets a person bring along, for now her own TensorX key; where it offers none, the entry goes.
  const external = useByok().data?.offered === true;
  const shown = external ? entries : entries.filter((e) => e.match !== "/settings/external");
  return (
    <WindowFrame kind="navigated" title="Settings" heading={shown.find((e) => pathname.startsWith(e.match))?.label} nav={<WindowNav label="Settings" entries={shown} />}>
      <div className="mx-auto flex w-full max-w-xl flex-col gap-6 p-4">
        <Outlet />
      </div>
    </WindowFrame>
  );
}
