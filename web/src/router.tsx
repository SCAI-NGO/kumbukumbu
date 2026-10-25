// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { useEffect } from "react";
import { Outlet, useLocation, useNavigate, type RouteObject } from "react-router-dom";
import { api, call } from "./api/client";
import { errorText } from "./api/error-text";
import ForgotPassword from "./routes/ForgotPassword";
import Join from "./routes/Join";
import SignIn from "./routes/SignIn";
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

/** The signed-in part of the app. A placeholder until the shell arrives. */
function RequireSession() {
  const session = useQuery({ queryKey: ["session"], queryFn: () => call(api.GET("/api/session")) });
  const signedIn = useSession((s) => s.signedIn);

  useEffect(() => {
    if (session.data) signedIn(session.data);
  }, [session.data, signedIn]);

  if (session.isPending) return null;
  // A 401 has already sent the app to sign-in; anything else is shown.
  if (session.isError) return <p className="p-4 text-text">{errorText(session.error)}</p>;
  return (
    <main className="mx-auto flex min-h-dvh max-w-xl flex-col justify-center gap-3 px-4">
      <h2 className="font-mono text-[11px] tracking-[0.08em] text-accent-text uppercase">Kumbu²</h2>
      <p className="text-text-body">Signed in as {session.data.user.display}. The notes arrive with the next tasks.</p>
    </main>
  );
}

export const routes: RouteObject[] = [
  {
    element: <Root />,
    children: [
      { path: "/join", element: <Join /> },
      { path: "/signin", element: <SignIn /> },
      { path: "/forgot", element: <ForgotPassword /> },
      {
        element: <RequireSession />,
        children: [
          { path: "/", element: null },
          { path: "/notes/:vault/*", element: null },
        ],
      },
    ],
  },
];
