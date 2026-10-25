// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { safeNext } from "./SignIn";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const path = (router: ReturnType<typeof renderApp>) => {
  const { pathname, search } = router.state.location;
  return pathname + search;
};

async function signIn() {
  await userEvent.type(screen.getByLabelText("Username"), "ada");
  await userEvent.type(screen.getByLabelText("Password"), "correct horse battery");
  await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
}

describe("a session that ends mid-use", () => {
  it("lands on sign-in with ?next= and a short message, and returns there after signing in", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    let signedIn = false;
    const fetch = fakeApi({
      "GET /api/session": () => (signedIn ? json(200, { user, previousSignInAt: null }) : json(401, { error: "Sign in first." })),
      "POST /api/session": () => {
        signedIn = true;
        return json(200, { user, previousSignInAt: null });
      },
      "GET /api/vaults": () => json(200, [{ name: "Arbeit", notes: 1 }]),
    });

    const router = renderApp("/notes/Arbeit/plan.md?line=4");

    expect(await screen.findByText("Your session has ended. Please sign in again.")).toBeInTheDocument();
    expect(path(router)).toBe(`/signin?next=${encodeURIComponent("/notes/Arbeit/plan.md?line=4")}`);

    await signIn();

    await waitFor(() => expect(path(router)).toBe("/notes/Arbeit/plan.md?line=4"));
    expect(await screen.findByRole("heading", { name: "plan" })).toBeInTheDocument();
    const body = await fetch.mock.calls.find(([r]) => r.method === "POST")![0].json();
    expect(body).toEqual({ username: "ada", password: "correct horse battery" });
  });

  it("doesn't take someone else signing in to the previous user's note", async () => {
    useSession.getState().signedIn({ user, previousSignInAt: null });
    useEditor.getState().openNote("/Arbeit/plan.md");
    const liz = { ...user, id: "u2", username: "liz", display: "Liz" };
    let signedIn = false;
    fakeApi({
      "GET /api/session": () => (signedIn ? json(200, { user: liz, previousSignInAt: null }) : json(401, { error: "Sign in first." })),
      "POST /api/session": () => {
        signedIn = true;
        return json(200, { user: liz, previousSignInAt: null });
      },
      "GET /api/vaults": () => json(200, [{ name: "Garten", notes: 1 }]),
    });

    const router = renderApp("/notes/Arbeit/plan.md");
    await screen.findByText("Your session has ended. Please sign in again.");
    await signIn();

    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    expect(path(router)).toBe("/");
    expect(useEditor.getState().recentPaths).toEqual([]);
  });

  it("sends a first visit to sign-in without the message", async () => {
    fakeApi({ "GET /api/session": () => json(401, { error: "Sign in first." }) });
    const router = renderApp("/");
    expect(await screen.findByRole("button", { name: "Sign in" })).toBeInTheDocument();
    expect(path(router)).toBe("/signin?next=%2F");
    expect(screen.queryByText(/session has ended/)).not.toBeInTheDocument();
  });

  it("never leaves the site after signing in", async () => {
    fakeApi({
      "POST /api/session": () => json(200, { user, previousSignInAt: null }),
      "GET /api/session": () => json(200, { user, previousSignInAt: null }),
      "GET /api/vaults": () => json(200, []),
    });
    const router = renderApp(`/signin?next=${encodeURIComponent("//evil.example/x")}`);
    await signIn();
    await waitFor(() => expect(path(router)).toBe("/"));
  });

  it("shows the server's sentence when sign-in fails", async () => {
    fakeApi({ "POST /api/session": () => json(401, { error: "The username or password is wrong." }) });
    renderApp("/signin");
    await signIn();
    expect(await screen.findByRole("alert")).toHaveTextContent("The username or password is wrong.");
  });
});

describe("safeNext", () => {
  it.each([
    ["/notes/Arbeit/plan.md?line=4", "/notes/Arbeit/plan.md?line=4"],
    ["/", "/"],
    [null, "/"],
    ["", "/"],
    ["//evil.example", "/"],
    ["/\\evil.example", "/"],
    ["https://evil.example/", "/"],
    ["javascript:alert(1)", "/"],
    ["notes/x", "/"],
  ])("%s → %s", (next, expected) => {
    expect(safeNext(next)).toBe(expected);
  });
});
