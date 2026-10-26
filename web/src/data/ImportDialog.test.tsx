// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { cleanup, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { strToU8, zipSync } from "fflate";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { versionOf } from "./readImport";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const zipFile = (entries: Record<string, string>, name = "Kumbu² export 2026-10-26.zip") =>
  new File([zipSync(Object.fromEntries(Object.entries(entries).map(([k, v]) => [k, strToU8(v)])))], name, { type: "application/zip" });

interface Upload {
  vault: string;
  files: { path: string; content: string }[];
  onConflict: string;
}

/** Work exists with a.md as in the ZIP and b.md otherwise; Ideas doesn't. */
async function server({ bDiffers = true } = {}) {
  const uploads: Upload[] = [];
  const created: string[] = [];
  const workVersions = [
    { path: "/Work/a.md", version: await versionOf("A") },
    { path: "/Work/b.md", version: await versionOf(bDiffers ? "old B" : "B") },
  ];
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2, embeddings: "internal", pending: false, canWrite: true }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/versions": () => json(200, { full: true, cursor: "c", state: "s", damaged: 0, notes: workVersions, deleted: [] }),
    "POST /api/vaults": async (request) => {
      const { name } = (await request.json()) as { name: string };
      created.push(name);
      return json(201, { name, notes: 0, embeddings: "internal", pending: false, canWrite: true });
    },
    "POST /api/upload/*": async (request) => {
      const body = (await request.json()) as { files: Upload["files"]; onConflict: string };
      const vault = decodeURIComponent(new URL(request.url).pathname.split("/")[3]);
      uploads.push({ vault, ...body });
      const replace = body.onConflict === "replace";
      return json(200, { written: replace ? 0 : body.files.length, replaced: replace ? body.files.length : 0, skipped: [], conflicts: [] });
    },
  });
  return { fetch, uploads, created };
}

const exported = { "Work/a.md": "A", "Work/b.md": "B", "Work/pic.png": "png", "Ideas/c.md": "C", "Ideas/d/e.md": "E" };

async function openAndPick(file: File, queryClient?: QueryClient) {
  renderApp("/settings", queryClient);
  const section = await screen.findByRole("region", { name: "Your data" });
  await userEvent.click(within(section).getByRole("button", { name: "Import vaults…" }));
  const dialog = await screen.findByRole("dialog", { name: "Import vaults" });
  await userEvent.upload(within(dialog).getByLabelText("Choose a ZIP"), file);
  return dialog;
}

describe("Import vaults… in Settings", () => {
  it("previews the vaults, asks once about the differing notes, and replaces them on Replace all", async () => {
    const { uploads, created } = await server();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    const dialog = await openAndPick(zipFile(exported), queryClient);

    const work = await within(dialog).findByRole("checkbox", { name: /Work/ });
    expect(work).toBeChecked();
    expect(within(dialog).getByRole("checkbox", { name: /Ideas/ })).toBeChecked();
    expect(within(work.closest("li")!).getByText("2 notes · exists: merge or skip")).toBeInTheDocument();
    expect(within(dialog).getByText("2 notes")).toBeInTheDocument();
    expect(within(dialog).getByText("1 file skipped")).toBeInTheDocument();

    await userEvent.click(within(dialog).getByRole("button", { name: "Import" }));

    const question = await within(dialog).findByText(/1 note in Work differs/);
    expect(question).toBeInTheDocument();
    expect(within(dialog).getByText(/the history keeps the old versions/)).toBeInTheDocument();
    expect(uploads).toEqual([]);
    expect(created).toEqual([]);

    await userEvent.click(within(dialog).getByRole("button", { name: "Replace all" }));

    expect(await within(dialog).findByText("2 vaults, 2 notes imported · 1 replaced")).toBeInTheDocument();
    expect(created).toEqual(["Ideas"]);
    expect(uploads).toEqual([
      { vault: "Work", files: [{ path: "b.md", content: "B" }], onConflict: "replace" },
      { vault: "Ideas", files: [{ path: "c.md", content: "C" }, { path: "d/e.md", content: "E" }], onConflict: "skip" },
    ]);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["vaults"] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["tree"] });
  });

  it("Skip all keeps the existing notes and says so", async () => {
    const { uploads } = await server();
    const dialog = await openAndPick(zipFile(exported));
    await userEvent.click(await within(dialog).findByRole("button", { name: "Import" }));
    await userEvent.click(await within(dialog).findByRole("button", { name: "Skip all" }));

    expect(await within(dialog).findByText("2 vaults, 2 notes imported · 1 kept as it was")).toBeInTheDocument();
    expect(uploads.map((u) => u.vault)).toEqual(["Ideas"]);
  });

  it("an unticked vault isn't touched", async () => {
    const { uploads, fetch } = await server();
    const dialog = await openAndPick(zipFile(exported));
    await userEvent.click(await within(dialog).findByRole("checkbox", { name: /Work/ }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Import" }));

    expect(await within(dialog).findByText("1 vault, 2 notes imported · ✓ no conflicts")).toBeInTheDocument();
    expect(uploads.map((u) => u.vault)).toEqual(["Ideas"]);
    expect(fetch.mock.calls.map(([r]) => new URL(r.url).pathname)).not.toContain("/api/vaults/Work/versions");
  });

  it("a second import of the same export changes nothing and asks nothing", async () => {
    const { uploads } = await server({ bDiffers: false });
    const dialog = await openAndPick(zipFile({ "Work/a.md": "A", "Work/b.md": "B" }));
    await userEvent.click(await within(dialog).findByRole("button", { name: "Import" }));

    expect(await within(dialog).findByText("1 vault, 0 notes imported · ✓ no conflicts")).toBeInTheDocument();
    expect(within(dialog).getByText("2 notes were there already, as they are.")).toBeInTheDocument();
    expect(within(dialog).queryByRole("button", { name: "Replace all" })).not.toBeInTheDocument();
    expect(uploads).toEqual([]);
  });

  it("shows a progress bar while the batches run, and a vault that failed with the server's sentence", async () => {
    let release!: () => void;
    const held = new Promise<void>((resolve) => (release = resolve));
    fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null }),
      "GET /api/vaults": () => json(200, []),
      "GET /api/embeddings": () => json(200, []),
      "POST /api/vaults": async (request) => {
        const { name } = (await request.json()) as { name: string };
        if (name === "Ideas") return json(400, { error: "A vault called Ideas can't be made here." });
        return json(201, { name, notes: 0, embeddings: "internal", pending: false, canWrite: true });
      },
      "POST /api/upload/*": async (request) => {
        const body = (await request.json()) as { files: unknown[] };
        await held;
        return json(200, { written: body.files.length, replaced: 0, skipped: [], conflicts: [] });
      },
    });
    const dialog = await openAndPick(zipFile(exported));
    await userEvent.click(await within(dialog).findByRole("button", { name: "Import" }));

    const bar = await within(dialog).findByRole("progressbar", { name: "Import progress" });
    expect(bar).toHaveAttribute("aria-valuemax", "4");
    expect(bar).toHaveAttribute("aria-valuenow", "0");
    release();

    expect(await within(dialog).findByText("1 vault, 2 notes imported · 1 vault failed")).toBeInTheDocument();
    expect(within(within(dialog).getByRole("list", { name: "Failed vaults" })).getByText("A vault called Ideas can't be made here.")).toBeInTheDocument();
  });

  it("a file that isn't a ZIP is refused before anything else", async () => {
    await server();
    const dialog = await openAndPick(new File(["hello"], "notes.zip"));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("This file isn't a ZIP");
  });
});
