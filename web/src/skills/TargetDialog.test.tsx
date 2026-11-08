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

const show = (mode: "copy" | "move", onClose = () => {}) =>
  render(
    <QueryClientProvider client={new QueryClient()}>
      <TargetDialog skill={skill} mode={mode} vaults={vaults} onClose={onClose} />
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
});
