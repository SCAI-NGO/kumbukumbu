// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { useEffect } from "react";
import { Link, Outlet, useLocation, useNavigate, type RouteObject } from "react-router-dom";
import { api, call } from "./api/client";
import { errorText } from "./api/error-text";
import ForgotPassword from "./routes/ForgotPassword";
import Join from "./routes/Join";
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
function RequireSession() {
  const session = useQuery({ queryKey: ["session"], queryFn: () => call(api.GET("/api/session")) });
  const signedIn = useSession((s) => s.signedIn);

  useEffect(() => {
    if (session.data) signedIn(session.data);
  }, [session.data, signedIn]);

  if (session.isPending) return null;
  // A 401 has already sent the app to sign-in; anything else is shown.
  if (session.isError) return <p className="p-4 text-text">{errorText(session.error)}</p>;
  return <Outlet />;
}

/** Keys, settings and admin: reached from the drawer's account row; their screens come with the next steps. */
function AccountPage({ title }: { title: string }) {
  return (
    <main className="mx-auto flex min-h-dvh max-w-xl flex-col gap-4 px-4 py-10">
      <Link to="/" className="flex min-h-11 items-center text-accent-text">
        Back to the notes
      </Link>
      <h1 className="text-[28px] leading-tight font-bold text-text">{title}</h1>
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
          {
            element: <MobileShell />,
            children: [
              { path: "/", element: null },
              { path: "/notes/:vault/*", element: null },
            ],
          },
          { path: "/keys", element: <AccountPage title="API keys" /> },
          { path: "/settings", element: <AccountPage title="Settings" /> },
          { path: "/admin/users", element: <AccountPage title="Users" /> },
          { path: "/admin/invitations", element: <AccountPage title="Invitations" /> },
        ],
      },
    ],
  },
];
