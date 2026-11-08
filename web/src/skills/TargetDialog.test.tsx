// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { VaultView } from "../api/notes";
import type { SkillView } from "../api/skills";
import { fakeApi, json } from "../test/app";
import TargetDialog from "./TargetDialog";

const skill: SkillView = { name: "x", description: "d", location: "Work", path: "/Work/_skills/x", source: null, lastEditor: null, byOther: false, warnings: [], canWrite: true };
const vault = (name: string, canWrite = true) => ({ name, notes: 1, embeddings: "", pending: false, canWrite, level: "owner", shared: false }) as VaultView;
const vaults = [vault("Archive"), vault("Skills"), vault("Work"), vault("Reading", false)];

const show = (mode: "copy" | "move", onClose = () => {}, client = new QueryClient(), given: VaultView[] | null = vaults) =>
  render(
    <QueryClientProvider client={client}>
      <TargetDialog skill={skill} mode={mode} vaults={given ?? undefined} onClose={onClose} />
    </QueryClientProvider>,
  );

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("TargetDialog", () => {
  it("offers the writable vaults but its own, Skills preselected", () => {
    show("copy");
    expect(screen.getAllByRole("radio").map((r) => (r as HTMLInputElement).value)).toEqual(["Archive", "Skills"]);
    expect(screen.getByRole("radio", { name: "Skills" })).toBeChecked();
    expect(screen.queryByText(/its files go to that vault's trash/)).not.toBeInTheDocument();
  });

  it("says where the files of a moved skill go", () => {
    show("move");
    expect(screen.getByText("The skill leaves Work; its files go to that vault's trash.")).toBeInTheDocument();
  });

  it("asks for another name when the name is taken, and sends it", async () => {
    const bodies: unknown[] = [];
    fakeApi({
      "POST /api/skills/copy": async (r) => {
        const body = await r.json();
        bodies.push(body);
        return body.name ? json(200, { path: "/Skills/y" }) : json(409, { error: "taken", type: "name-taken" });
      },
    });
    const onClose = vi.fn();
    show("copy", onClose);
    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    const field = await screen.findByLabelText("Name");
    await userEvent.clear(field);
    await userEvent.type(field, "y");
    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(bodies).toEqual([{ from: "/Work/_skills/x", to: "Skills" }, { from: "/Work/_skills/x", to: "Skills", name: "y" }]);
  });

  it("keeps the name field and the chosen name after another error", async () => {
    const bodies: { name?: string }[] = [];
    let n = 0;
    fakeApi({
      "POST /api/skills/copy": async (r) => {
        bodies.push(await r.json());
        return ++n === 1 ? json(409, { error: "taken", type: "name-taken" }) : n === 2 ? json(507, { error: "No room." }) : json(200, { path: "/Skills/y" });
      },
    });
    const onClose = vi.fn();
    show("copy", onClose);
    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    const field = await screen.findByLabelText("Name");
    await userEvent.clear(field);
    await userEvent.type(field, "y");
    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    expect(await screen.findByText("No room.")).toBeInTheDocument();
    expect(screen.getByLabelText("Name")).toHaveValue("y");
    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(bodies.map((b) => b.name)).toEqual([undefined, "y", "y"]);
  });

  it("drops the name field when another vault is chosen", async () => {
    fakeApi({ "POST /api/skills/copy": () => json(409, { error: "taken", type: "name-taken" }) });
    show("copy");
    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    await screen.findByLabelText("Name");
    await userEvent.click(screen.getByRole("radio", { name: "Archive" }));
    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
  });

  it("moves with POST /api/skills/move", async () => {
    let body: unknown;
    fakeApi({ "POST /api/skills/move": async (r) => ((body = await r.json()), json(200, { path: "/Skills/x" })) });
    const onClose = vi.fn();
    show("move", onClose);
    await userEvent.click(screen.getByRole("button", { name: "Move" }));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(body).toEqual({ from: "/Work/_skills/x", to: "Skills" });
  });

  it("refreshes the skills, trees and vault list after a copy", async () => {
    fakeApi({ "POST /api/skills/copy": () => json(200, { path: "/Skills/x" }) });
    const client = new QueryClient();
    const spy = vi.spyOn(client, "invalidateQueries");
    const onClose = vi.fn();
    show("copy", onClose, client);
    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    const keys = spy.mock.calls.map((c) => (c[0]?.queryKey as string[])[0]);
    expect(keys).toEqual(expect.arrayContaining(["skills", "tree", "vaults"]));
  });

  it("says it is loading while the vaults are", () => {
    show("copy", () => {}, new QueryClient(), null);
    expect(screen.getByText("Loading…")).toBeInTheDocument();
    expect(screen.queryByText(/no other vault/)).not.toBeInTheDocument();
  });
});
