// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { VaultView } from "../api/notes";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { skillCodeKey } from "./AddSkill";
import { scriptNotice } from "./InstallDialog";

// Made up for the test, in the shape of a link's code.
const code = "ABCD-EFGH-JKLM-NPQR-STUV-WXYZ";
const notice = "This adds a copy. For your safety it is never updated automatically; you'll be told when there's a newer version and can review it first.";

const vault = (name: string, canWrite = true, kind?: string) => ({ name, notes: 1, embeddings: "", pending: false, canWrite, level: "owner", shared: false, kind }) as VaultView;
const vaults = [vault("Garden"), vault("Skills", true, "skills"), vault("Reading", false), vault("Work")];

// The skill behind the code, as the add dialog's preview gives it.
const shown = "2026-11-10T10:00:00.1234567Z";
const preview = {
  name: "write-as-alice",
  description: "Writes posts in Alice's voice.",
  manifest: "---\nname: write-as-alice\ndescription: Writes posts in Alice's voice.\n---\n# Write\n\nKeep every post short.\n",
  snapshotAt: shown,
  files: [
    { index: 0, path: "SKILL.md", mediaType: "text/markdown", size: 120 },
    { index: 1, path: "scripts/count.py", mediaType: "text/plain", size: 2048 },
  ],
  hasScripts: true,
  copy: "c0",
};
const gone = "This link doesn't open a shared skill: the code is wrong, or the skill is no longer shared.";
const changed = "This skill was changed since it was shown; look at it again before adding.";

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
    "POST /api/skills/add/preview": () => json(200, preview),
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
    expect(await bodies(fetch)).toEqual([{ code, vault: "Skills", shown }]);
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
    expect(await bodies(fetch)).toEqual([{ code, vault: "Work", shown }]);
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
      { code, vault: "Skills", shown },
      { code, vault: "Skills", shown, name: "alices-posts" },
    ]);
    expect(screen.getByRole("link", { name: "To your skills" })).toHaveAttribute("href", "/skills/installed");
  });

  it("shows the skill's name, description, text, files and the scripts notice before Add", async () => {
    const { fetch } = addApi();
    renderApp(`/skills/add#${code}`);

    const dialog = await screen.findByRole("dialog", { name: "Add to my skills" });
    await waitFor(() => expect(screen.getByRole("button", { name: "Add" })).toBeEnabled());
    expect(within(dialog).getByText("write-as-alice")).toBeInTheDocument();
    expect(within(dialog).getByText("Writes posts in Alice's voice.")).toBeInTheDocument();
    expect(within(dialog).getByText("Keep every post short.")).toBeInTheDocument();
    expect(within(dialog).getByText("scripts/count.py")).toBeInTheDocument();
    expect(within(dialog).getByText("2.0 KB")).toBeInTheDocument();
    expect(within(dialog).getByText(scriptNotice)).toBeInTheDocument();
    expect(await bodies(fetch)).toEqual([]);
    // The code goes in the body, never in a path or a query.
    const asked = fetch.mock.calls.map(([r]) => r as Request).filter((r) => new URL(r.url).pathname === "/api/skills/add/preview");
    expect(await Promise.all(asked.map((r) => r.clone().json()))).toEqual([{ code }]);
  });

  it("keeps Add disabled until the preview has loaded", async () => {
    let answer: (r: Response) => void = () => {};
    addApi({ "POST /api/skills/add/preview": () => new Promise<Response>((resolve) => (answer = resolve)) });
    renderApp(`/skills/add#${code}`);

    const add = await screen.findByRole("button", { name: "Add" });
    await waitFor(() => expect(screen.getByRole("radio", { name: "Skills" })).toBeChecked());
    expect(add).toBeDisabled();
    answer(json(200, preview));
    await waitFor(() => expect(add).toBeEnabled());
    expect(screen.getByText("Keep every post short.")).toBeInTheDocument();
  });

  it("reloads the preview with a notice when the skill changed since it was shown", async () => {
    const later = "2026-11-10T11:00:00Z";
    let previews = 0;
    let answer: (r: Response) => void = () => {};
    let adds = 0;
    const { fetch } = addApi({
      "POST /api/skills/add/preview": () => (++previews === 1 ? json(200, preview) : new Promise<Response>((resolve) => (answer = resolve))),
      "POST /api/skills/add": () => (++adds === 1 ? json(409, { error: changed, type: "skill-changed" }) : json(201, { path: "/Skills/write-as-alice" })),
    });
    renderApp(`/skills/add#${code}`);

    const add = await screen.findByRole("button", { name: "Add" });
    await waitFor(() => expect(add).toBeEnabled());
    await userEvent.click(add);

    expect(await screen.findByText(changed)).toBeInTheDocument();
    await waitFor(() => expect(previews).toBe(2));
    expect(add).toBeDisabled();
    answer(json(200, { ...preview, snapshotAt: later, manifest: "# Write\n\nKeep every post long.\n" }));
    expect(await screen.findByText("Keep every post long.")).toBeInTheDocument();
    expect(screen.queryByText("Keep every post short.")).toBeNull();
    expect(screen.getByText(changed)).toBeInTheDocument();
    await waitFor(() => expect(add).toBeEnabled());

    await userEvent.click(add);
    expect(await screen.findByText(/write-as-alice is in Skills now/)).toBeInTheDocument();
    expect(await bodies(fetch)).toEqual([
      { code, vault: "Skills", shown },
      { code, vault: "Skills", shown: later },
    ]);
  });

  it("shows the skill page's answer for a code that is gone", async () => {
    addApi({ "POST /api/skills/add/preview": () => json(404, { error: gone }) });
    renderApp(`/skills/add#${code}`);

    expect(await screen.findByText(gone)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add" })).toBeDisabled();
    expect(sessionStorage.getItem(skillCodeKey)).toBe(code);
  });

  it("shows the skill page's answer when the code went while she looked", async () => {
    let previews = 0;
    addApi({
      "POST /api/skills/add/preview": () => (++previews === 1 ? json(200, preview) : json(404, { error: gone })),
      "POST /api/skills/add": () => json(409, { error: changed, type: "skill-changed" }),
    });
    renderApp(`/skills/add#${code}`);

    const add = await screen.findByRole("button", { name: "Add" });
    await waitFor(() => expect(add).toBeEnabled());
    await userEvent.click(add);

    expect(await screen.findByText(gone)).toBeInTheDocument();
    expect(screen.queryByText("Keep every post short.")).toBeNull();
    expect(add).toBeDisabled();
  });
});
