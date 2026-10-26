// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { lazy, Suspense, useEffect, type ReactNode } from "react";
import { Outlet, useLocation, useNavigate, useSearchParams, type RouteObject } from "react-router-dom";
import { api, call } from "./api/client";
import { errorText } from "./api/error-text";
import { RouteError } from "./components/AppError";
import ForgotPassword from "./routes/ForgotPassword";
import SignIn from "./routes/SignIn";
import MobileShell from "./shell/MobileShell";
import { useSession } from "./state/session";

/** Opts into the v7 behaviour now, which also keeps React Router's warnings out of the console. */
export const routerFuture = {
  v7_relativeSplatPath: true,
  v7_fetcherPersist: true,
  v7_normalizeFormMethod: true,
  v7_partialHydration: true,
  v7_skipActionErrorRevalidation: true,
};

/** Sends to sign-in, with the way back, as soon as any request found the session gone. */
function Root() {
  const ended = useSession((s) => s.ended);
  const navigate = useNavigate();
  const { pathname, search } = useLocation();

  useEffect(() => {
    if (!ended) return;
    useSession.setState({ ended: false });
    if (pathname !== "/signin") navigate(`/signin?next=${encodeURIComponent(pathname + search)}`, { replace: true });
  }, [ended, pathname, search, navigate]);

  return <Outlet />;
}

/** The signed-in part of the app. */
function RequireSession({ children }: { children?: ReactNode }) {
  const session = useQuery({ queryKey: ["session"], queryFn: () => call(api.GET("/api/session")) });
  const signedIn = useSession((s) => s.signedIn);

  useEffect(() => {
    if (session.data) signedIn(session.data);
  }, [session.data, signedIn]);

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
      { path: "/signin", element: <SignIn /> },
      { path: "/forgot", element: <ForgotPassword /> },
      { path: "/consent", element: <ConsentRoute /> },
      {
        element: <RequireSession />,
        // A page that fails takes only its own place; Root still sends a session that ended to sign-in.
        errorElement: <RouteError />,
        children: [
          {
            element: <MobileShell />,
            children: [
              { path: "/", element: null },
              { path: "/notes/:vault/*", element: null },
            ],
          },
          { path: "/keys", lazy: lazyPage(() => import("./routes/Keys")) },
          { path: "/settings", lazy: lazyPage(() => import("./routes/Settings")) },
          { path: "/admin/users", lazy: lazyPage(() => import("./routes/admin/Users")) },
          { path: "/admin/invitations", lazy: lazyPage(() => import("./routes/admin/Invitations")) },
        ],
      },
    ],
  },
];
