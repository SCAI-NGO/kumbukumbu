// SPDX-License-Identifier: AGPL-3.0-or-later
import { Fragment, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { Link, Navigate, useNavigate, useParams } from "react-router-dom";
import { AccountScreen } from "../components/form";
import Connections from "./Connections";
import Keys from "./Keys";

const tabs = [
  { id: "keys", label: "API keys" },
  { id: "apps", label: "Apps" },
  { id: "dav", label: "WebDAV" },
] as const;

type Tab = (typeof tabs)[number]["id"];

/** Each tab's content; one that shows a secret calls `onReveal` (see `useReveal`) so the tabs make way for it. */
const panels: Record<Tab, (onReveal: (revealing: boolean) => void) => ReactNode> = {
  keys: (onReveal) => <Keys onRevealChange={onReveal} />,
  apps: (onReveal) => <Connections subtitle="MCP via OAuth: claude.ai connectors, ChatGPT and others" onRevealChange={onReveal} />,
  dav: () => (
    <section aria-labelledby="dav-title" className="flex min-w-0 flex-col gap-6">
      <h2 id="dav-title" className="text-[22px] font-bold text-text">
        WebDAV
      </h2>
      <p className="text-text-body">WebDAV shares will be listed here.</p>
    </section>
  ),
};

const tabClass = (on: boolean) =>
  `flex min-h-11 flex-1 items-center justify-center rounded-[10px] px-3 text-sm font-medium focus-visible:outline-2 focus-visible:outline-accent ${on ? "bg-accent text-on-accent" : "text-text-body"}`;

/** The ways into the user's vaults, a tab each, the tab in the URL so it can be linked to and survives a reload. */
export default function Access() {
  const { tab } = useParams();
  const navigate = useNavigate();
  const refs = useRef<Partial<Record<Tab, HTMLAnchorElement | null>>>({});
  // The tab whose secret is on show: it has the page to itself until done, so no other tab drops it unseen.
  const [revealing, setRevealing] = useState<Tab | null>(null);

  const index = tabs.findIndex((t) => t.id === tab);
  if (index < 0) return <Navigate to="/access/keys" replace />;
  const current = tabs[index];

  const onKeyDown = (e: KeyboardEvent) => {
    const to = { ArrowLeft: index - 1, ArrowRight: index + 1, Home: 0, End: tabs.length - 1 }[e.key];
    if (to === undefined) return;
    e.preventDefault();
    const next = tabs[(to + tabs.length) % tabs.length];
    // Moving along the tabs is one step, as for any tablist; Back leaves the page rather than walking the tabs.
    navigate(`/access/${next.id}`, { replace: true });
    refs.current[next.id]?.focus();
  };

  const onReveal = (revealed: boolean) => setRevealing(revealed ? current.id : null);
  const alone = revealing === current.id;

  return (
    <AccountScreen title="Access">
      {!alone && (
        <div role="tablist" aria-label="Access" onKeyDown={onKeyDown} className="flex gap-1 rounded-[12px] border border-line p-1">
          {tabs.map((t) => (
            <Link
              key={t.id}
              ref={(el) => (refs.current[t.id] = el)}
              to={`/access/${t.id}`}
              role="tab"
              id={`access-tab-${t.id}`}
              aria-selected={t.id === current.id}
              aria-controls={t.id === current.id ? "access-panel" : undefined}
              tabIndex={t.id === current.id ? 0 : -1}
              className={tabClass(t.id === current.id)}
            >
              {t.label}
            </Link>
          ))}
        </div>
      )}
      {/* A panel of no tab while the tabs are away for a reveal. */}
      <div
        role={alone ? undefined : "tabpanel"}
        id="access-panel"
        aria-labelledby={alone ? undefined : `access-tab-${current.id}`}
        className="flex min-w-0 flex-col gap-6"
      >
        {/* Keyed, so a tab's state goes with it. */}
        <Fragment key={current.id}>{panels[current.id](onReveal)}</Fragment>
      </div>
    </AccountScreen>
  );
}
