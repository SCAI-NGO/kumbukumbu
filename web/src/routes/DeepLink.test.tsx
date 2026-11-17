// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const sentence = "This link leads to a note that isn't here: it was moved or removed, or it's in a vault you can't see right now.";

const vault = (name: string) => ({ id: "v1", name, notes: 2, embeddings: "ready", pending: false, canWrite: true });
const entry = (path: string, kind = "note") => ({ name: path.split("/").at(-1), path, size: 1, kind, mediaType: "text/markdown" });
const tree = (name: string, notes: ReturnType<typeof entry>[], folders: unknown[] = []) => ({
  root: { name, path: `/${name}`, folders, notes },
});

function api(name: string, root = tree(name, [entry(`/${name}/Roses.md`), entry(`/${name}/rose.png`, "image")], [
  { name: "Garten Ü", path: `/${name}/Garten Ü`, folders: [], notes: [entry(`/${name}/Garten Ü/Rosen #1.md`)] },
])) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [vault(name)]),
    [`GET /api/vaults/${name}/tree`]: () => json(200, root),
  });
}

const path = (router: ReturnType<typeof renderApp>) => router.state.location.pathname;

describe("DeepLink", () => {
  it("resolves_to_the_owners_name", async () => {
    api("gartenarbeit");
    const router = renderApp("/n/v1/Roses.md");
    await waitFor(() => expect(path(router)).toBe("/notes/gartenarbeit/Roses.md"));
  });

  it("resolves_to_a_members_alias", async () => {
    api("garten");
    const router = renderApp("/n/v1/Roses.md");
    await waitFor(() => expect(path(router)).toBe("/notes/garten/Roses.md"));
  });

  it("a_file_goes_to_the_lightbox", async () => {
    api("gartenarbeit");
    const router = renderApp("/n/v1/rose.png");
    await waitFor(() => expect(path(router)).toBe("/view/gartenarbeit/rose.png"));
  });

  it("decodes_the_path", async () => {
    api("gartenarbeit");
    const router = renderApp("/n/v1/Garten%20%C3%9C/Rosen%20%231.md");
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/gartenarbeit/Garten%20%C3%9C/Rosen%20%231.md"));
  });

  it("unknown_id_shows_the_one_page", async () => {
    api("gartenarbeit");
    renderApp("/n/nope/Roses.md");
    expect(await screen.findByRole("heading", { name: "Not here" })).toBeInTheDocument();
    expect(screen.getByText(sentence)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Search for it" }));
    expect(useEditor.getState().searchQuery).toBe("Roses");
    expect(useEditor.getState().overlay).toBe("search");
    expect(await screen.findByRole("dialog", { name: "Search" })).toBeInTheDocument();
    expect(screen.getByRole("searchbox", { name: "Search" })).toHaveValue("Roses");
  });

  it("missing_path_shows_the_one_page", async () => {
    api("gartenarbeit");
    renderApp("/n/v1/Garten%20%C3%9C/Gone.md");
    expect(await screen.findByRole("heading", { name: "Not here" })).toBeInTheDocument();
    expect(screen.getByText(sentence)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Search for it" }));
    expect(useEditor.getState().searchQuery).toBe("Gone");
    expect(useEditor.getState().overlay).toBe("search");
    expect(await screen.findByRole("dialog", { name: "Search" })).toBeInTheDocument();
    expect(screen.getByRole("searchbox", { name: "Search" })).toHaveValue("Gone");
  });

  it("a_vault_whose_tree_is_refused_shows_the_one_page", async () => {
    fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null }),
      "GET /api/vaults": () => json(200, [vault("gartenarbeit")]),
      "GET /api/vaults/gartenarbeit/tree": () => json(404, { error: "No such vault." }),
    });
    renderApp("/n/v1/Roses.md");
    expect(await screen.findByText(sentence)).toBeInTheDocument();
  });

  it("search_for_it_beats_the_remembered_note_on_a_fresh_load", async () => {
    localStorage.setItem("kumbu.lastNote.u1", "/gartenarbeit/Roses.md");
    api("gartenarbeit");
    const router = renderApp("/n/nope/Gone.md");
    await userEvent.click(await screen.findByRole("button", { name: "Search for it" }));
    expect(await screen.findByRole("dialog", { name: "Search" })).toBeInTheDocument();
    // Let the kept note's tree arrive: the start must not carry the reader off to it.
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    await new Promise((r) => setTimeout(r, 100));
    expect(path(router)).toBe("/");
    expect(screen.getByRole("dialog", { name: "Search" })).toBeInTheDocument();
  });

  it("the_address_is_replaced", async () => {
    api("gartenarbeit");
    const router = renderApp(["/", "/n/v1/Roses.md"]);
    await waitFor(() => expect(path(router)).toBe("/notes/gartenarbeit/Roses.md"));
    await router.navigate(-1);
    expect(path(router)).toBe("/");
  });
});
