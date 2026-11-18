// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { lazy, Suspense, useEffect, type JSX, type ReactNode } from "react";
import { Navigate, Outlet, useLocation, useMatches, useNavigate, useSearchParams, type RouteObject } from "react-router-dom";
import { api, call } from "./api/client";
import { useZonePreset } from "./calendar/preferences";
import { errorText } from "./api/error-text";
import { RouteError } from "./components/AppError";
import PasswordPrompt from "./components/PasswordPrompt";
import { followSession, useDiscreetMode, useLatestPathname } from "./discreet/hideAgain";
import { discreetUrl, NEUTRAL, showsDiscreet } from "./discreet/neutralWindow";
import { followVaults } from "./discreet/state";
import ForgotPassword from "./routes/ForgotPassword";
import { Moved, SettingsMoved } from "./routes/settings/redirects";
import SignIn from "./routes/SignIn";
import { useEditor } from "./state/editor";
import { useSession } from "./state/session";
import { useApplyUpdates } from "./update/apply";
import UpdateBar from "./update/UpdateBar";
import { setWindowRoutes } from "./windows/store";
import WindowHost from "./windows/WindowHost";

/**
 * Sends to sign-in, with the way back, as soon as any request found the session gone; and reloads into a new
 * release, signed in or not.
 */
function Root() {
  const ended = useSession((s) => s.ended);
  const navigate = useNavigate();
  const { pathname, search } = useLocation();
  useApplyUpdates();

  useEffect(() => {
    if (!ended) return;
    useSession.setState({ ended: false });
    // Write mode ends with the session: the editor keeps its unsaved text as a draft, and the note opens again
    // after signing in (in Write mode, with the draft). Left on, Write mode would outlive the editor.
    useEditor.setState({ mode: "read", currentPath: null });
    // Never the way back into a discreet view: the session's end has forgotten which vaults are discreet already.
    const neutral = pathname === NEUTRAL || discreetUrl(pathname) || showsDiscreet();
    if (pathname !== "/signin") navigate(`/signin?next=${encodeURIComponent(neutral ? "/" : pathname + search)}`, { replace: true });
  }, [ended, pathname, search, navigate]);

  // The shell shows the update bar under its own top bar, under a window too; every other page has it above.
  const inShell = useMatches().some((m) => (m.handle as { shell?: boolean } | undefined)?.shell === true);
  return (
    <>
      {!inShell && <UpdateBar />}
      <Outlet />
      <PasswordPrompt />
    </>
  );
}

/** The signed-in part of the app. */
function RequireSession({ children }: { children?: ReactNode }) {
  const session = useQuery({ queryKey: ["session"], queryFn: () => call(api.GET("/api/session")) });
  const signedIn = useSession((s) => s.signedIn);
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const pathname = useLatestPathname();
  useDiscreetMode();
  // Here, not in the shell: a deep link to the calendar presets the zone too.
  useZonePreset();

  useEffect(() => followVaults(queryClient), [queryClient]);

  useEffect(() => {
    if (session.data) signedIn(session.data);
  }, [session.data, signedIn]);

  // Each read: one answering as the last did is the same object, and may still need following (a hide unsent).
  useEffect(() => {
    if (session.data) followSession(queryClient, navigate, pathname(), session.data.discreet);
  }, [session.data, session.dataUpdatedAt, queryClient, navigate, pathname]);

  if (session.isPending) return null;
  // A 401 has already sent the app to sign-in; anything else is shown.
  if (session.isError) return <p className="p-4 text-text">{errorText(session.error)}</p>;
  return children ?? <Outlet />;
}

const Consent = lazy(() => import("./routes/Consent"));
const ConsentError = lazy(() => import("./routes/Consent").then((m) => ({ default: m.ConsentError })));

/** The app's consent page; an error it found before trusting the app needs no session, a request does. */
function ConsentRoute() {
  const [params] = useSearchParams();
  const error = params.get("error");
  return (
    <Suspense fallback={null}>
      {error !== null ? (
        <ConsentError code={error} />
      ) : (
        <RequireSession>
          <Consent />
        </RequireSession>
      )}
    </Suspense>
  );
}

/** Pages loaded on first visit only, which keeps them out of the main bundle. */
const lazyPage = (load: () => Promise<{ default: () => JSX.Element }>) => async () => ({ Component: (await load()).default });

export const routes: RouteObject[] = [
  {
    element: <Root />,
    errorElement: <RouteError />,
    // Blank while a lazy page loads on the first visit.
    HydrateFallback: () => null,
    children: [
      { path: "/join", lazy: lazyPage(() => import("./routes/Join")) },
      // Signed in or not: without a session it says what to do first, and keeps the link's secret through sign-in.
      { path: "/share", lazy: lazyPage(() => import("./routes/Share")) },
      // As /share: a shared skill's code waits through sign-in. Not a window: it is before the app (and outranks /skills/:tab).
      { path: "/skills/add", lazy: lazyPage(() => import("./skills/AddSkill")) },
      { path: "/signin", element: <SignIn /> },
      { path: "/forgot", element: <ForgotPassword /> },
      { path: "/consent", element: <ConsentRoute /> },
      // A discreet view's address: one this tab no longer remembers (a reload, a hide) shows the vault list.
      { path: NEUTRAL, element: <Navigate to="/" replace /> },
      {
        element: <RequireSession />,
        // A page that fails takes only its own place; Root still sends a session that ended to sign-in.
        errorElement: <RouteError />,
        children: [
          {
            element: <WindowHost />,
            handle: { shell: true },
            children: [
              { path: "/", element: null },
              { path: "/notes/:vault/*", element: null },
              { path: "/whats-on", handle: { window: true }, lazy: lazyPage(() => import("./calendar/WhatsOnPage")) },
              // Across the vaults the session shows: no vault in the address, so none of a discreet one reaches it.
              { path: "/recent", handle: { window: true }, lazy: lazyPage(() => import("./recent/RecentPage")) },
              { path: "/console", handle: { window: true }, lazy: lazyPage(() => import("./console/ConsolePage")) },
              {
                lazy: lazyPage(() => import("./routes/settings/SettingsWindow")),
                handle: { window: true },
                children: [
                  { path: "/settings/appearance", lazy: lazyPage(() => import("./routes/settings/Appearance")) },
                  { path: "/settings/vaults", lazy: lazyPage(() => import("./routes/settings/Vaults")) },
                  { path: "/settings/access/:tab", lazy: lazyPage(() => import("./routes/Access")) },
                  { path: "/settings/access", element: <Navigate to="/settings/access/keys" replace /> },
                  { path: "/settings/skills/:tab", lazy: lazyPage(() => import("./routes/Skills")) },
                  { path: "/settings/skills", element: <Navigate to="/settings/skills/installed" replace /> },
                  { path: "/settings/calendar", lazy: lazyPage(() => import("./routes/settings/Calendar")) },
                  { path: "/settings/security", lazy: lazyPage(() => import("./routes/settings/Security")) },
                  { path: "/settings/data", lazy: lazyPage(() => import("./routes/settings/Data")) },
                  { path: "/settings/about", lazy: lazyPage(() => import("./routes/settings/About")) },
                ],
              },
              {
                lazy: lazyPage(() => import("./routes/admin/AdminWindow")),
                handle: { window: true },
                children: [
                  { path: "/admin/users", lazy: lazyPage(() => import("./routes/admin/Users")) },
                  { path: "/admin/invitations", lazy: lazyPage(() => import("./routes/admin/Invitations")) },
                ],
              },
              // The addresses before the settings were a window, in bookmarks and older links: windows too, so that
              // the shell stays behind while they are replaced.
              { path: "/settings", handle: { window: true }, element: <SettingsMoved /> },
              { path: "/access/:tab", handle: { window: true }, element: <Moved to="access" /> },
              { path: "/access", handle: { window: true }, element: <Navigate to="/settings/access/keys" replace /> },
              { path: "/keys", handle: { window: true }, element: <Navigate to="/settings/access/keys" replace /> },
              { path: "/skills/:tab", handle: { window: true }, element: <Moved to="skills" /> },
              { path: "/skills", handle: { window: true }, element: <Navigate to="/settings/skills/installed" replace /> },
            ],
          },
          { path: "/n/:vaultId/*", lazy: lazyPage(() => import("./routes/DeepLink")) },
          { path: "/checkpoints/:vault/:name/*", lazy: lazyPage(() => import("./routes/Checkpoint")) },
          { path: "/board/:vault", lazy: lazyPage(() => import("./board/BoardPage")) },
          { path: "/calendar/:vault", lazy: lazyPage(() => import("./calendar/CalendarPage")) },
          { path: "/gallery/:vault", lazy: lazyPage(() => import("./attachments/GalleryPage")) },
          { path: "/view/:vault/*", lazy: lazyPage(() => import("./attachments/Lightbox")) },
        ],
      },
    ],
  },
];
setWindowRoutes(routes);
