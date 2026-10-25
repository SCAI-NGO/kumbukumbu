// SPDX-License-Identifier: AGPL-3.0-or-later
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const code = "inv_s3cr3t-code";
const valid = { role: "user", expiresAt: "2026-11-01T00:00:00Z", valid: true };

afterEach(() => {
  vi.unstubAllGlobals();
  useSession.getState().reset();
});

describe("Join", () => {
  it("reads the code from the fragment, clears it, and never puts it into a path or query", async () => {
    const fetch = fakeApi({
      "POST /api/invitations/peek": () => json(200, valid),
      "POST /api/join": () => json(200, { user, recoveryKey: "RK-1234" }),
    });
    const router = renderApp(`/join#${code}`);

    expect(await screen.findByRole("button", { name: "Create account" })).toBeInTheDocument();
    expect(router.state.location.hash).toBe("");
    expect(router.state.location.pathname).toBe("/join");

    await userEvent.type(screen.getByLabelText("Username"), "ada");
    await userEvent.type(screen.getByLabelText("Password"), "correct horse battery");
    await userEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(await screen.findByText("RK-1234")).toBeInTheDocument();

    const requests = fetch.mock.calls.map(([r]) => r);
    expect(requests.map((r) => r.url).some((u) => u.includes(code))).toBe(false);
    expect(await requests[0].json()).toEqual({ code });
    expect(await requests[1].json()).toEqual({ code, username: "ada", password: "correct horse battery" });
    expect(router.state.location.search).toBe("");
  });

  it("says why an invitation can't be used", async () => {
    fakeApi({ "POST /api/invitations/peek": () => json(200, { ...valid, valid: false, reason: "expired" }) });
    renderApp(`/join#${code}`);
    expect(await screen.findByText("This invitation has expired. Ask for a new one.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Create account" })).not.toBeInTheDocument();
  });

  it("says so when the link has no code", async () => {
    fakeApi({});
    renderApp("/join");
    expect(await screen.findByText("This link has no invitation in it. Open the full link you were sent.")).toBeInTheDocument();
  });

  it("shows the server's sentence when joining is refused", async () => {
    fakeApi({
      "POST /api/invitations/peek": () => json(200, valid),
      "POST /api/join": () => json(409, { error: "The username 'ada' is taken." }),
    });
    renderApp(`/join#${code}`);
    await userEvent.type(await screen.findByLabelText("Username"), "ada");
    await userEvent.type(screen.getByLabelText("Password"), "correct horse battery");
    await userEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The username 'ada' is taken.");
  });

  it("enters the app only after the recovery key is saved and confirmed", async () => {
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    fakeApi({
      "POST /api/invitations/peek": () => json(200, valid),
      "POST /api/join": () => json(200, { user, recoveryKey: "RK-1234" }),
      "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    });
    const router = renderApp(`/join#${code}`);
    await userEvent.type(await screen.findByLabelText("Username"), "ada");
    await userEvent.type(screen.getByLabelText("Password"), "correct horse battery");
    await userEvent.click(screen.getByRole("button", { name: "Create account" }));

    await userEvent.click(await screen.findByRole("button", { name: "Copy" }));
    await userEvent.click(screen.getByLabelText(/I have stored my recovery key/));
    await userEvent.click(screen.getByRole("button", { name: "Continue" }));

    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(await screen.findByText(/Signed in as Ada/)).toBeInTheDocument();
  });
});
