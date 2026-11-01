// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { shareSecretKey } from "./Share";

// Made up for the test, as the server would make one: 32 bytes, Base64Url.
const secret = "dGhpcy1pcy1hLXRlc3Qtc2VjcmV0LW9mLTMyLWI";

const peek = {
  vault: "Gartenarbeit",
  owner: '"Alice Example"',
  ownerUsername: "alice",
  role: "write",
  embeddingProvider: "internal",
  alias: "Gartenarbeit",
  aliasFree: true,
  confirm: false,
  expiresAt: "2026-11-08T10:00:00Z",
};
const internal = { name: "internal", label: "On this server", description: null, external: false, available: true, reason: null, default: true };

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  sessionStorage.clear();
  useSession.getState().reset();
  useEditor.getState().reset();
});

function shareApi(extra: Parameters<typeof fakeApi>[0] = {}, signedIn = true) {
  const state = { signedIn };
  const fetch = fakeApi({
    "GET /api/session": () => (state.signedIn ? json(200, { user, previousSignInAt: null }) : json(401, { error: "Sign in first." })),
    "GET /api/embeddings": () => json(200, [internal]),
    "GET /api/vaults": () => json(200, []),
    "POST /api/vault-invitations/peek": () => json(200, peek),
    ...extra,
  });
  return { fetch, state };
}

const urls = (fetch: ReturnType<typeof fakeApi>) => fetch.mock.calls.map(([r]) => (r as Request).url);
const bodies = (fetch: ReturnType<typeof fakeApi>, path: string) =>
  Promise.all(fetch.mock.calls.map(([r]) => r as Request).filter((r) => new URL(r.url).pathname === path).map((r) => r.clone().json()));

describe("the share page", () => {
  it("keeps the secret in sessionStorage and never in the URL after sign-in", async () => {
    const { fetch, state } = shareApi({ "POST /api/session": () => ((state.signedIn = true), json(200, { user, previousSignInAt: null })) }, false);
    const router = renderApp(`/share#${secret}`);

    const signIn = await screen.findByRole("link", { name: "Sign in" });
    expect(router.state.location.hash).toBe("");
    expect(router.state.location.pathname).toBe("/share");
    expect(sessionStorage.getItem(shareSecretKey)).toBe(secret);
    expect(signIn).toHaveAttribute("href", "/signin?next=%2Fshare");

    await userEvent.click(signIn);
    await userEvent.type(await screen.findByLabelText("Username"), "ada");
    await userEvent.type(screen.getByLabelText("Password"), "correct horse");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByRole("heading", { name: "Join Gartenarbeit" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/share");
    expect(router.state.location.search).toBe("");
    expect(router.state.location.hash).toBe("");
    expect(await bodies(fetch, "/api/vault-invitations/peek")).toEqual([{ secret }]);
    // Neither a path nor a query ever held it.
    expect(urls(fetch).some((u) => u.includes(secret))).toBe(false);
  });

  it("asks for another alias on collision", async () => {
    let accepts = 0;
    const { fetch } = shareApi({
      "POST /api/vault-invitations/peek": async (r) => {
        const body = (await r.json()) as { alias?: string };
        return json(200, { ...peek, alias: body.alias ?? peek.alias, aliasFree: body.alias === "Beete" });
      },
      "POST /api/vault-invitations/accept": () =>
        accepts++ === 0 ? json(409, { error: "You already have a vault named 'Garten'." }) : json(200, { id: "m1", vault: "Beete", role: "write", status: "active" }),
    });
    renderApp(`/share#${secret}`);

    const alias = await screen.findByLabelText("Your name for this vault");
    expect(alias).toHaveValue("Gartenarbeit");
    expect(screen.getByText("You have a vault named Gartenarbeit already. Choose another name for this one.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Accept" })).toBeDisabled();

    await userEvent.clear(alias);
    await userEvent.type(alias, "Garten");
    await userEvent.click(screen.getByRole("button", { name: "Accept" }));
    // The server found that name taken too: peek, asked about it, says so; the link stays for another try.
    expect(await screen.findByText("You have a vault named Garten already. Choose another name for this one.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Accept" })).toBeDisabled();
    expect(sessionStorage.getItem(shareSecretKey)).toBe(secret);

    await userEvent.clear(alias);
    await userEvent.type(alias, "Beete");
    await userEvent.click(screen.getByRole("button", { name: "Accept" }));
    expect(await screen.findByRole("heading", { name: "Beete is shared with you" })).toBeInTheDocument();
    expect(await bodies(fetch, "/api/vault-invitations/accept")).toEqual([
      { secret, alias: "Garten" },
      { secret, alias: "Beete" },
    ]);
    expect(sessionStorage.getItem(shareSecretKey)).toBeNull();
  });

  it("shows owner display name and username separately", async () => {
    shareApi({ "POST /api/vault-invitations/peek": () => json(200, { ...peek, role: "maintain" }) });
    renderApp(`/share#${secret}`);
    const field = async (term: string) => (await screen.findByText(term, { selector: "dt" })).nextElementSibling;
    expect(await field("Vault")).toHaveTextContent("Gartenarbeit");
    expect(await field("Owner")).toHaveTextContent(/^"Alice Example"$/);
    expect(await field("Owner's username")).toHaveTextContent(/^alice$/);
    expect(await field("Your role")).toHaveTextContent("Maintain");
    expect(await field("Your role")).toHaveClass("text-warning");
    expect(await field("Search by meaning")).toHaveTextContent("On this server");
  });

  it("tells someone without an account what to do, and keeps the link", async () => {
    shareApi({}, false);
    renderApp(`/share#${secret}`);
    expect(
      await screen.findByText(/^To accept, you need an account on this Kumbu² first\. If you have none, tell the person who sent you the link that you haven't been invited yet/),
    ).toBeInTheDocument();
    // Not sent to sign-in by itself: the page is read first.
    await waitFor(() => expect(screen.getByRole("link", { name: "Sign in" })).toBeInTheDocument());
    expect(useSession.getState().ended).toBe(false);
  });

  it("says when the link holds no invitation, or one that is no longer valid", async () => {
    shareApi({ "POST /api/vault-invitations/peek": () => json(404, { error: "This invitation link is not valid (any more)." }) });
    renderApp("/share");
    expect(await screen.findByText("This link has no invitation in it. Open the full link you were sent.")).toBeInTheDocument();
    cleanup();
    renderApp(`/share#${secret}`);
    expect(await screen.findByText("This invitation link is not valid (any more).")).toBeInTheDocument();
  });

  it("accepts an invitation the owner confirms, and says she does so first", async () => {
    shareApi({
      "POST /api/vault-invitations/peek": () => json(200, { ...peek, confirm: true }),
      "POST /api/vault-invitations/accept": () => json(200, { id: "m1", vault: "Gartenarbeit", role: "write", status: "pending" }),
    });
    renderApp(`/share#${secret}`);
    expect(await screen.findByText("The owner confirms your access after you accept.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Accept" }));
    expect(await screen.findByText("The owner confirms your access first. Gartenarbeit shows among your vaults once they have.")).toBeInTheDocument();
    expect(sessionStorage.getItem(shareSecretKey)).toBeNull();
  });
});
