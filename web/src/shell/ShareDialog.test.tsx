// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { maintainSentence } from "../routes/rights";
import { fakeApi, json } from "../test/app";
import ShareDialog from "./ShareDialog";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

// Made up for the test, as the server would make it: the secret in the fragment.
const link = "https://kumbu.example/share#c2VjcmV0LWZvci10aGUtdGVzdA";
const created = { id: "m1", link, role: "write", confirm: false, expiresAt: "2026-11-08T10:00:00Z" };

/** The dialog behind a button, so it can be closed and opened again; with the query client it shares. */
function renderShare(queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
  function Harness() {
    const [open, setOpen] = useState(true);
    return (
      <>
        <button type="button" onClick={() => setOpen(true)}>
          Open
        </button>
        {open && <ShareDialog vault="Gartenarbeit" onClose={() => setOpen(false)} />}
      </>
    );
  }
  render(
    <QueryClientProvider client={queryClient}>
      <Harness />
    </QueryClientProvider>,
  );
  return queryClient;
}

async function fill(label = "Bob", password = "correct horse") {
  await userEvent.type(screen.getByLabelText("Who it is for"), label);
  await userEvent.type(screen.getByLabelText("Your password"), password);
}

describe("ShareDialog", () => {
  it("keeps password managers away from the label field", () => {
    fakeApi({});
    renderShare();
    const field = screen.getByLabelText("Who it is for");
    expect(field).toHaveAttribute("data-bwignore");
    expect(field).toHaveAttribute("data-1p-ignore");
    expect(field).toHaveAttribute("data-lpignore");
  });

  it("shows the link once and forgets it on close", async () => {
    let body: unknown;
    fakeApi({
      "POST /api/vaults/Gartenarbeit/invitations": async (r) => {
        body = await r.json();
        return json(201, created);
      },
    });
    const queryClient = renderShare();
    await fill();
    await userEvent.click(screen.getByRole("button", { name: "Create link" }));

    expect(await screen.findByLabelText("Invitation link")).toHaveTextContent(link);
    expect(body).toEqual({ password: "correct horse", label: "Bob", role: "write", expiresInDays: 7, confirm: false });
    expect(screen.getByText(/This link is shown only now\. Send it through a safe channel/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Copy link" })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    // Nothing kept it: not the caches, and not the dialog opened again.
    await waitFor(() => expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state))).not.toContain("share#"));
    expect(JSON.stringify(queryClient.getQueryCache().getAll().map((q) => q.state.data))).not.toContain("share#");
    await userEvent.click(screen.getByRole("button", { name: "Open" }));
    expect(screen.getByRole("button", { name: "Create link" })).toBeInTheDocument();
    expect(screen.queryByLabelText("Invitation link")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Your password")).toHaveValue("");
  });

  it("shows maintain in the warning colour", async () => {
    fakeApi({});
    renderShare();
    const maintain = screen.getByRole("radio", { name: "Maintain" });
    expect(maintain.closest("label")).toHaveClass("text-warning");
    expect(screen.getByRole("radio", { name: "Read and write" })).toBeChecked();
    expect(screen.queryByText(maintainSentence)).not.toBeInTheDocument();
    await userEvent.click(maintain);
    expect(screen.getByText(maintainSentence)).toHaveClass("text-warning");
    expect(maintainSentence).toBe("Can change the vault's structure: the board's columns, checkpoints.");
  });

  it("needs the password", async () => {
    const fetch = fakeApi({ "POST /api/vaults/Gartenarbeit/invitations": () => json(400, { error: "The password is wrong." }) });
    renderShare();
    await userEvent.type(screen.getByLabelText("Who it is for"), "Bob");
    expect(screen.getByRole("button", { name: "Create link" })).toBeDisabled();
    await userEvent.type(screen.getByLabelText("Your password"), "wrong");
    await userEvent.click(screen.getByRole("button", { name: "Create link" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The password is wrong.");
    expect(screen.queryByLabelText("Invitation link")).not.toBeInTheDocument();
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("sends the role, the expiry and the confirmation chosen, and says who gets in", async () => {
    let body: unknown;
    fakeApi({
      "POST /api/vaults/Gartenarbeit/invitations": async (r) => {
        body = await r.json();
        return json(201, { ...created, role: "read", confirm: true });
      },
    });
    renderShare();
    await fill("Carol");
    await userEvent.click(screen.getByRole("radio", { name: "Read" }));
    await userEvent.click(screen.getByRole("radio", { name: "30 days" }));
    await userEvent.click(screen.getByRole("checkbox", { name: /Ask me to confirm before access is granted/ }));
    await userEvent.click(screen.getByRole("button", { name: "Create link" }));
    await screen.findByLabelText("Invitation link");
    expect(body).toEqual({ password: "correct horse", label: "Carol", role: "read", expiresInDays: 30, confirm: true });
    expect(screen.getByText(/whoever opens it first asks you for access/)).toBeInTheDocument();
  });

  it("says what members with write can do, and that new members see what was there before", () => {
    fakeApi({});
    renderShare();
    expect(
      screen.getByText((_, el) => el?.tagName === "P" && el.textContent === "Members with write can change and delete notes. Deleted notes stay in the trash for 30 days, and earlier versions in the history, as far as its limits go."),
    ).toBeInTheDocument();
    expect(screen.getByText("New members see everything in the vault, also what was written before they joined.")).toBeInTheDocument();
  });
});
