// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { VaultView } from "../api/notes";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { skillCodeKey } from "./AddSkill";

// Made up for the test, in the shape of a link's code.
const code = "ABCD-EFGH-JKLM-NPQR-STUV-WXYZ";
const notice = "This adds a copy. For your safety it is never updated automatically; you'll be told when there's a newer version and can review it first.";

const vault = (name: string, canWrite = true, kind?: string) => ({ name, notes: 1, embeddings: "", pending: false, canWrite, level: "owner", shared: false, kind }) as VaultView;
const vaults = [vault("Garden"), vault("Skills", true, "skills"), vault("Reading", false), vault("Work")];

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  sessionStorage.clear();
  useSession.getState().reset();
  useEditor.getState().reset();
});

function addApi(extra: Parameters<typeof fakeApi>[0] = {}, signedIn = true) {
  const state = { signedIn };
  const fetch = fakeApi({
    "GET /api/session": () => (state.signedIn ? json(200, { user, previousSignInAt: null }) : json(401, { error: "Sign in first." })),
    "GET /api/vaults": () => json(200, vaults),
    "POST /api/skills/add": () => json(201, { path: "/Skills/write-as-alice" }),
    ...extra,
  });
  return { fetch, state };
}

const urls = (fetch: ReturnType<typeof fakeApi>) => fetch.mock.calls.map(([r]) => (r as Request).url);
const bodies = (fetch: ReturnType<typeof fakeApi>) =>
  Promise.all(fetch.mock.calls.map(([r]) => r as Request).filter((r) => new URL(r.url).pathname === "/api/skills/add").map((r) => r.clone().json()));

describe("Add to my skills", () => {
  it("moves the code out of the address and keeps it through sign-in", async () => {
    const { fetch, state } = addApi({ "POST /api/session": () => ((state.signedIn = true), json(200, { user, previousSignInAt: null })) }, false);
    const router = renderApp(`/skills/add#${code}`);

    const signIn = await screen.findByRole("link", { name: "Sign in" });
    expect(router.state.location.hash).toBe("");
    expect(router.state.location.pathname).toBe("/skills/add");
    expect(sessionStorage.getItem(skillCodeKey)).toBe(code);
    expect(signIn).toHaveAttribute("href", "/signin?next=%2Fskills%2Fadd");

    await userEvent.click(signIn);
    await userEvent.type(await screen.findByLabelText("Username"), "ada");
    await userEvent.type(screen.getByLabelText("Password"), "correct horse");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByRole("dialog", { name: "Add to my skills" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/skills/add");
    expect(router.state.location.search).toBe("");
    expect(router.state.location.hash).toBe("");
    await userEvent.click(await screen.findByRole("button", { name: "Add" }));
    expect(await screen.findByText(/write-as-alice is in Skills now/)).toBeInTheDocument();
    expect(await bodies(fetch)).toEqual([{ code, vault: "Skills" }]);
    // Neither a path nor a query ever held it.
    expect(urls(fetch).some((u) => u.includes(code))).toBe(false);
  });

  it("shows the safety notice and adds to Skills by default", async () => {
    const { fetch } = addApi({ "POST /api/skills/add": () => json(201, { path: "/Work/_skills/write-as-alice" }) });
    renderApp(`/skills/add#${code}`);

    expect(await screen.findByText(notice)).toBeInTheDocument();
    // Every vault she may write, members' too; Skills preselected.
    await waitFor(() => expect(screen.getAllByRole("radio").map((r) => (r as HTMLInputElement).value)).toEqual(["Garden", "Skills", "Work"]));
    expect(screen.getByRole("radio", { name: "Skills" })).toBeChecked();
    await userEvent.click(screen.getByRole("radio", { name: "Work" }));
    await userEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(await screen.findByText(/write-as-alice is in Work now/)).toBeInTheDocument();
    expect(await bodies(fetch)).toEqual([{ code, vault: "Work" }]);
  });

  it("forgets the code on Cancel", async () => {
    const { fetch } = addApi({ "GET /api/skills": () => json(200, { skills: [], withoutSkills: [] }), "GET /api/skills/shared": () => json(200, []) });
    const router = renderApp(`/skills/add#${code}`);

    await userEvent.click(await screen.findByRole("button", { name: "Cancel" }));

    expect(sessionStorage.getItem(skillCodeKey)).toBeNull();
    await waitFor(() => expect(router.state.location.pathname).toBe("/skills/installed"));
    expect(await bodies(fetch)).toEqual([]);
  });

  it("forgets the code once added", async () => {
    let n = 0;
    const { fetch } = addApi({
      "POST /api/skills/add": () =>
        ++n === 1 ? json(409, { error: "/Skills/write-as-alice is taken; choose another name.", type: "name-taken" }) : json(201, { path: "/Skills/alices-posts" }),
    });
    renderApp(`/skills/add#${code}`);

    await userEvent.click(await screen.findByRole("button", { name: "Add" }));
    // A taken name keeps the code: she chooses another name and tries again.
    const field = await screen.findByLabelText("Name");
    expect(sessionStorage.getItem(skillCodeKey)).toBe(code);
    await userEvent.type(field, "alices-posts");
    await userEvent.click(screen.getByRole("button", { name: "Add" }));

    expect(await screen.findByText(/alices-posts is in Skills now/)).toBeInTheDocument();
    expect(sessionStorage.getItem(skillCodeKey)).toBeNull();
    expect(await bodies(fetch)).toEqual([
      { code, vault: "Skills" },
      { code, vault: "Skills", name: "alices-posts" },
    ]);
    expect(screen.getByRole("link", { name: "To your skills" })).toHaveAttribute("href", "/skills/installed");
  });
});
