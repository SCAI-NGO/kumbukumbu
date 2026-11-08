// SPDX-License-Identifier: AGPL-3.0-or-later
import { Fragment, useRef, type KeyboardEvent, type ReactNode } from "react";
import { Link, Navigate, useNavigate, useParams } from "react-router-dom";
import { usePublishEnabled } from "../api/publications";
import { AccountScreen } from "../components/form";
import Installed from "../skills/Installed";
import Shared from "../skills/Shared";
import Try from "../skills/Try";

const allTabs = [
  { id: "installed", label: "Installed" },
  { id: "try", label: "Try" },
  { id: "shared", label: "Shared" },
] as const;

type Tab = (typeof allTabs)[number]["id"];

/** Each tab's content. */
const panels: Record<Tab, () => ReactNode> = {
  installed: () => <Installed />,
  try: () => <Try />,
  shared: () => <Shared />,
};

const tabClass = (on: boolean) =>
  `flex min-h-11 flex-1 items-center justify-center rounded-[10px] px-3 text-sm font-medium focus-visible:outline-2 focus-visible:outline-accent ${on ? "bg-accent text-on-accent" : "text-text-body"}`;

/** The user's skills, a tab each, the tab in the URL so it can be linked to and survives a reload. */
export default function Skills() {
  const { tab } = useParams();
  const navigate = useNavigate();
  const refs = useRef<Partial<Record<Tab, HTMLAnchorElement | null>>>({});
  // Shared only where the server publishes; until the session says, the tab waits rather than flicker.
  const publishEnabled = usePublishEnabled();
  const tabs = allTabs.filter((t) => t.id !== "shared" || publishEnabled === true);

  if (tab === "shared" && publishEnabled === undefined) {
    return (
      <AccountScreen title="Skills">
        <p className="text-text-body">Loading…</p>
      </AccountScreen>
    );
  }
  const index = tabs.findIndex((t) => t.id === tab);
  if (index < 0) return <Navigate to="/skills/installed" replace />;
  const current = tabs[index];

  const onKeyDown = (e: KeyboardEvent) => {
    const to = { ArrowLeft: index - 1, ArrowRight: index + 1, Home: 0, End: tabs.length - 1 }[e.key];
    if (to === undefined) return;
    e.preventDefault();
    const next = tabs[(to + tabs.length) % tabs.length];
    // Moving along the tabs is one step, as for any tablist; Back leaves the page rather than walking the tabs.
    navigate(`/skills/${next.id}`, { replace: true });
    refs.current[next.id]?.focus();
  };

  return (
    <AccountScreen title="Skills">
      <div role="tablist" aria-label="Skills" onKeyDown={onKeyDown} className="flex gap-1 rounded-[12px] border border-line p-1">
          {tabs.map((t) => (
            <Link
              key={t.id}
              ref={(el) => (refs.current[t.id] = el)}
              to={`/skills/${t.id}`}
              role="tab"
              id={`skills-tab-${t.id}`}
              aria-selected={t.id === current.id}
              aria-controls={t.id === current.id ? "skills-panel" : undefined}
              tabIndex={t.id === current.id ? 0 : -1}
              className={tabClass(t.id === current.id)}
            >
              {t.label}
            </Link>
          ))}
      </div>
      <div
        role="tabpanel"
        id="skills-panel"
        aria-labelledby={`skills-tab-${current.id}`}
        className="flex min-w-0 flex-col gap-6"
      >
        {/* Keyed, so a tab's state goes with it. */}
        <Fragment key={current.id}>{panels[current.id]()}</Fragment>
      </div>
    </AccountScreen>
  );
}
