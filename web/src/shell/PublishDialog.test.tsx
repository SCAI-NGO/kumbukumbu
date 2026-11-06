// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json, user } from "../test/app";
import PublishDialog from "./PublishDialog";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const NOTE = "/Work/Questions.md";
// Made up for the test, as the server would make it: the code in the fragment.
const link = "https://kumbu.example.org/s#ABCDEFGHJKMNPQRSTVWXYZ0123456789";
const published = { link, snapshotAt: "2026-11-05T14:32:00Z", plain: [] as string[] };

const session = (passwordWindowOpen: boolean) => () => json(200, { user, previousSignInAt: null, passwordWindowOpen, publishEnabled: true });

function renderDialog(onClose = vi.fn()) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <PublishDialog path={NOTE} onClose={onClose} />
    </QueryClientProvider>,
  );
  return onClose;
}

describe("PublishDialog", () => {
  it("asks for the password when the window is closed", async () => {
    let body: unknown;
    fakeApi({
      "GET /api/session": session(false),
      "POST /api/publications/Work/Questions.md": async (r) => {
        body = await r.json();
        return json(201, published);
      },
      "GET /api/publications/Work/Questions.md": () => json(200, { path: NOTE, snapshotAt: published.snapshotAt, older: false, changedAt: null, publishedBy: "you", link }),
      "GET /api/publications": () => json(200, []),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    });
    renderDialog();
    expect(screen.getByRole("heading", { name: "Publish this note" })).toBeInTheDocument();
    expect(screen.getByText("Anyone with the link can read a copy of this note as it is now. Links to other notes become plain text.")).toBeInTheDocument();
    await userEvent.type(await screen.findByLabelText("Your password"), "correct horse");
    await userEvent.click(screen.getByRole("button", { name: "Publish" }));
    expect(await screen.findByLabelText("Link")).toHaveValue(link);
    expect(body).toEqual({ password: "correct horse" });
  });

  it("asks nothing when it is open", async () => {
    let body: unknown;
    fakeApi({
      "GET /api/session": session(true),
      "POST /api/publications/Work/Questions.md": async (r) => {
        body = await r.json();
        return json(201, published);
      },
      "GET /api/publications/*": () => json(404, { error: "Work/Questions.md is not published." }),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    });
    renderDialog();
    const publish = await screen.findByRole("button", { name: "Publish" });
    // The fresh answer about the window is in before the button can be used.
    await vi.waitFor(() => expect(publish).toBeEnabled());
    expect(screen.queryByLabelText("Your password")).not.toBeInTheDocument();
    await userEvent.click(publish);
    expect(await screen.findByLabelText("Link")).toHaveValue(link);
    expect(body).toEqual({});
  });

  it("shows the link and copies it", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    fakeApi({
      "GET /api/session": session(true),
      "POST /api/publications/Work/Questions.md": () => json(201, published),
      "GET /api/publications/*": () => json(404, { error: "Work/Questions.md is not published." }),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    });
    const onClose = renderDialog();
    const publish = await screen.findByRole("button", { name: "Publish" });
    await vi.waitFor(() => expect(publish).toBeEnabled());
    await userEvent.click(publish);
    const field = await screen.findByLabelText("Link");
    expect(field).toHaveAttribute("readonly");
    await userEvent.click(screen.getByRole("button", { name: "Copy link" }));
    expect(writeText).toHaveBeenCalledTimes(1);
    expect(writeText).toHaveBeenCalledWith(link);
    expect(screen.queryByText(/Not copied/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(onClose).toHaveBeenCalled();
  });

  it("names what became plain text", async () => {
    fakeApi({
      "GET /api/session": session(true),
      "POST /api/publications/Work/Questions.md": () => json(201, { ...published, plain: ["report.pdf", "Private.png", "PDFs are not published yet"] }),
      "GET /api/publications/*": () => json(404, { error: "Work/Questions.md is not published." }),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    });
    renderDialog();
    const publish = await screen.findByRole("button", { name: "Publish" });
    await vi.waitFor(() => expect(publish).toBeEnabled());
    await userEvent.click(publish);
    expect(await screen.findByText("Not copied, plain text instead: report.pdf, Private.png.")).toBeInTheDocument();
    expect(screen.getByText("PDFs are not published yet.")).toBeInTheDocument();
  });

  it("shows a refusal's sentence", async () => {
    fakeApi({
      "GET /api/session": session(true),
      "POST /api/publications/Work/Questions.md": () => json(403, { error: "Work/Questions.md is unpublishable.", code: "unpublishable" }),
      "GET /api/publications/*": () => json(404, { error: "Work/Questions.md is not published." }),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    });
    renderDialog();
    const publish = await screen.findByRole("button", { name: "Publish" });
    await vi.waitFor(() => expect(publish).toBeEnabled());
    await userEvent.click(publish);
    expect(await screen.findByText("Work/Questions.md is unpublishable.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Link")).not.toBeInTheDocument();
  });

  it("keeps the password in no cache, refused or not", async () => {
    let refuse = true;
    fakeApi({
      "GET /api/session": session(false),
      "POST /api/publications/Work/Questions.md": () => (refuse ? json(403, { error: "The password is wrong." }) : json(201, published)),
      "GET /api/publications/*": () => json(404, { error: "Work/Questions.md is not published." }),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <PublishDialog path={NOTE} onClose={vi.fn()} />
      </QueryClientProvider>,
    );
    const cached = () => JSON.stringify(queryClient.getMutationCache().getAll().map((m) => [m.state.variables, m.state.data]));
    await userEvent.type(await screen.findByLabelText("Your password"), "correct horse");
    await userEvent.click(screen.getByRole("button", { name: "Publish" }));
    expect(await screen.findByText("The password is wrong.")).toBeInTheDocument();
    expect(cached()).not.toContain("correct horse");

    refuse = false;
    await userEvent.click(screen.getByRole("button", { name: "Publish" }));
    expect(await screen.findByLabelText("Link")).toHaveValue(link);
    expect(cached()).not.toContain("correct horse");
    expect(cached()).not.toContain(link);
  });
});
