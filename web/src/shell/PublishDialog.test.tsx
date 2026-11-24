// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json, user } from "../test/app";
import { pdfsNotYet } from "../api/publications";
import PublishDialog from "./PublishDialog";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const NOTE = "/Work/Questions.md";
// Made up for the test, as the server would make it: the code in the fragment.
const link = "https://kumbu.example.org/s#ABCDEFGHJKMNPQRSTVWXYZ0123456789";
const published = { link, snapshotAt: "2026-11-05T14:32:00Z", plain: [] as string[] };

// The list at depth 0: the note alone.
const alone = () => json(200, { notes: [{ path: NOTE, title: "Questions", depth: 0 }], leftOut: [], plain: [], bytes: 0, tooMany: false, tooLarge: false });

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
      "GET /api/publication-previews/Work/Questions.md": alone,
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
      "GET /api/publication-previews/Work/Questions.md": alone,
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
      "GET /api/publication-previews/Work/Questions.md": alone,
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
      "GET /api/publication-previews/Work/Questions.md": alone,
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
      "GET /api/publication-previews/Work/Questions.md": alone,
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
      "GET /api/publication-previews/Work/Questions.md": alone,
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

  describe("with the notes it links to", () => {
    const preview = (paths: string[], extra: Record<string, unknown> = {}) => ({
      notes: paths.map((path, i) => ({ path, title: path.split("/").at(-1)!.replace(".md", ""), depth: i === 0 ? 0 : 1 })),
      leftOut: [{ path: "/Work/Salaries.md", reason: "unpublishable" }],
      plain: [],
      bytes: 0,
      tooMany: false,
      tooLarge: false,
      ...extra,
    });

    /** The item of Stays behind whose line (the path and the reason, not its button) reads `text`. */
    const behindItem = (text: string) =>
      vi.waitFor(() => {
        const item = within(screen.getByRole("list", { name: "Stays behind" }))
          .getAllByRole("listitem")
          .find((li) => li.firstElementChild?.textContent === text);
        expect(item, text).toBeDefined();
        return item!;
      });

    const base = (handlers: Parameters<typeof fakeApi>[0]) =>
      fakeApi({
        "GET /api/session": session(true),
        "GET /api/publications/*": () => json(404, { error: "Work/Questions.md is not published." }),
        "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
        ...handlers,
      });

    it("opens with the depth selected, so that a digit replaces it", async () => {
      base({ "GET /api/publication-previews/Work/Questions.md": () => json(200, preview([NOTE, "/Work/Plan.md"])) });
      renderDialog();
      const depth = await screen.findByLabelText<HTMLInputElement>("How many links deep");
      await vi.waitFor(() => expect(document.activeElement).toBe(depth));
      // A number field keeps no selection range in the DOM: typing proves the value was selected.
      await userEvent.keyboard("1");
      expect(depth).toHaveValue(1);
      expect(await screen.findByText("+1 note")).toBeInTheDocument();
    });

    it("lists the note itself at depth 0 too", async () => {
      const asked: string[] = [];
      base({
        "GET /api/publication-previews/Work/Questions.md": (r) => {
          asked.push(new URL(r.url).search);
          return json(200, preview([NOTE], { leftOut: [] }));
        },
      });
      renderDialog();
      const box = await within(await screen.findByRole("group", { name: "What goes out" })).findByRole("checkbox", { name: /Questions/ });
      expect(box).toBeChecked();
      expect(box).toBeDisabled();
      expect(screen.getByText("This note only")).toBeInTheDocument();
      expect(asked).toEqual(["?depth=0"]);
    });

    it("shows the list, what stays behind and sends what it showed", async () => {
      let body: unknown;
      const asked: string[] = [];
      base({
        "GET /api/publication-previews/Work/Questions.md": (r) => {
          asked.push(new URL(r.url).search);
          return json(200, preview([NOTE, "/Work/Plan.md", "/Work/Budget.md"]));
        },
        "POST /api/publications/Work/Questions.md": async (r) => {
          body = await r.json();
          return json(201, published);
        },
      });
      renderDialog();
      const depth = await screen.findByLabelText("How many links deep");
      expect(screen.getByText("This note only")).toBeInTheDocument();
      await userEvent.clear(depth);
      await userEvent.type(depth, "1");
      expect(await screen.findByText("+2 notes")).toBeInTheDocument();
      expect(screen.getByText(/only reached through|marked unpublishable/)).toBeInTheDocument();
      expect(asked).toEqual(["?depth=0", "?depth=1"]);
      const publish = screen.getByRole("button", { name: "Publish 3 notes" });
      await vi.waitFor(() => expect(publish).toBeEnabled());
      await userEvent.click(publish);
      expect(await screen.findByLabelText("Link")).toHaveValue(link);
      expect(body).toEqual({ depth: 1, without: [], expected: ["Budget.md", "Plan.md", "Questions.md"] });
    });

    it("takes a note out when its box is cleared", async () => {
      const asked: string[] = [];
      base({
        "GET /api/publication-previews/Work/Questions.md": (r) => {
          const query = new URL(r.url).search;
          asked.push(query);
          return json(200, preview(query.includes("without") ? [NOTE] : [NOTE, "/Work/Plan.md"]));
        },
      });
      renderDialog();
      await userEvent.type(await screen.findByLabelText("How many links deep"), "{backspace}1");
      await userEvent.click(await screen.findByRole("checkbox", { name: /Plan/ }));
      await vi.waitFor(() => expect(asked.at(-1)).toBe("?depth=1&without=Plan.md"));
      expect(await screen.findByRole("button", { name: "Publish 1 note" })).toBeInTheDocument();
      expect(screen.getByRole("checkbox", { name: /Questions/ })).toBeDisabled();
    });

    it("lists an unticked note as taken out by you, and takes it back in", async () => {
      const asked: string[] = [];
      base({
        "GET /api/publication-previews/Work/Questions.md": (r) => {
          const query = new URL(r.url).search;
          asked.push(query);
          // As the server answers: the unticked note is left out with the reason "without".
          return json(
            200,
            query.includes("without")
              ? preview([NOTE], { leftOut: [{ path: "/Work/Salaries.md", reason: "unpublishable" }, { path: "/Work/Beta.md", reason: "without" }] })
              : preview([NOTE, "/Work/Beta.md"]),
          );
        },
      });
      renderDialog();
      await userEvent.type(await screen.findByLabelText("How many links deep"), "{backspace}1");
      await userEvent.click(await screen.findByRole("checkbox", { name: /Beta/ }));
      await vi.waitFor(() => expect(asked.at(-1)).toBe("?depth=1&without=Beta.md"));
      const item = await behindItem("/Work/Beta.md: taken out by you");
      expect(within(screen.getByRole("group", { name: "What goes out" })).queryByRole("checkbox", { name: /Beta/ })).not.toBeInTheDocument();
      const back = within(item).getByRole("button", { name: "Take back in" });
      expect(back).toHaveClass("min-h-12");
      // The button names its note to a screen reader, and has the focus the box had.
      expect(back).toHaveAccessibleDescription("/Work/Beta.md: taken out by you");
      await vi.waitFor(() => expect(document.activeElement).toBe(back));
      await userEvent.click(back);
      await vi.waitFor(() => expect(asked.at(-1)).toBe("?depth=1"));
      const box = await within(screen.getByRole("group", { name: "What goes out" })).findByRole("checkbox", { name: /Beta/ });
      expect(box).toBeChecked();
      await vi.waitFor(() => expect(document.activeElement).toBe(box));
      expect(screen.queryByText(/taken out by you/)).not.toBeInTheDocument();
    });

    it("leaves the focus where the person put it when the list after a toggle never came", async () => {
      base({
        "GET /api/publication-previews/Work/Questions.md": (r) => {
          const query = new URL(r.url).search;
          // The list without Beta fails; at depth 2 it answers with Beta taken out.
          if (query === "?depth=1&without=Beta.md") return json(500, { error: "The server failed." });
          return json(
            200,
            query.includes("without")
              ? preview([NOTE], { leftOut: [{ path: "/Work/Beta.md", reason: "without" }] })
              : preview([NOTE, "/Work/Beta.md"]),
          );
        },
      });
      renderDialog();
      const depth = await screen.findByLabelText("How many links deep");
      await userEvent.type(depth, "{backspace}1");
      await userEvent.click(await screen.findByRole("checkbox", { name: /Beta/ }));
      await screen.findByRole("alert");
      await userEvent.type(depth, "{backspace}2");
      await behindItem("/Work/Beta.md: taken out by you");
      expect(document.activeElement, "still in the depth field").toBe(depth);
    });

    it("lists a note taken out that the preview doesn't name, so it can be taken back in", async () => {
      base({ "GET /api/publication-previews/Work/Questions.md": () => json(200, preview([NOTE])) });
      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      render(
        <QueryClientProvider client={queryClient}>
          <PublishDialog path={NOTE} onClose={vi.fn()} update={{ depth: 1, without: ["/Work/Gone.md"] }} />
        </QueryClientProvider>,
      );
      const item = await behindItem("/Work/Gone.md: taken out by you");
      expect(within(item).getByRole("button", { name: "Take back in" })).toBeInTheDocument();
    });

    it("lists the PDFs that stay behind, by name", async () => {
      base({
        "GET /api/publication-previews/Work/Questions.md": () =>
          json(200, preview([NOTE, "/Work/Plan.md"], { leftOut: [], plain: ["manual.pdf", "photo.png", "Scan.PDF", pdfsNotYet] })),
      });
      renderDialog();
      await userEvent.type(await screen.findByLabelText("How many links deep"), "{backspace}1");
      const list = await screen.findByRole("list", { name: "Stays behind" });
      expect(within(list).getAllByRole("listitem").map((li) => li.textContent)).toEqual(["manual.pdf: a PDF (not published)", "Scan.PDF: a PDF (not published)"]);
      expect(screen.queryByText(/photo\.png/)).not.toBeInTheDocument();
      expect(screen.queryByText(new RegExp(pdfsNotYet))).not.toBeInTheDocument();
    });

    it("warns beside the depth from 50 notes on, not at 49", async () => {
      let notes = 50;
      base({
        "GET /api/publication-previews/Work/Questions.md": () => json(200, preview([NOTE, ...Array.from({ length: notes - 1 }, (_, i) => `/Work/n${i}.md`)])),
      });
      renderDialog();
      await userEvent.type(await screen.findByLabelText("How many links deep"), "{backspace}1");
      expect(await screen.findByText("+49 notes")).toHaveClass("font-semibold", "text-warning");
      notes = 49;
      await userEvent.type(screen.getByLabelText("How many links deep"), "{backspace}2");
      const hint = await screen.findByText("+48 notes");
      expect(hint).toHaveClass("text-text-body");
      expect(hint).not.toHaveClass("text-warning");
    });

    it("warns from 50 notes on", async () => {
      base({
        "GET /api/publication-previews/Work/Questions.md": () => json(200, preview([NOTE, ...Array.from({ length: 49 }, (_, i) => `/Work/n${i}.md`)])),
      });
      renderDialog();
      await userEvent.type(await screen.findByLabelText("How many links deep"), "{backspace}2");
      expect(await screen.findByText("That's a lot of notes. Check the list before you publish.")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Publish 50 notes" })).toBeInTheDocument();
    });

    it("shows the new list when the vault changed meanwhile", async () => {
      base({
        "GET /api/publication-previews/Work/Questions.md": () => json(200, preview([NOTE, "/Work/Plan.md"])),
        "POST /api/publications/Work/Questions.md": () =>
          json(409, { message: "Something changed in the vault since the list was shown: check the list again.", preview: preview([NOTE, "/Work/Plan.md", "/Work/New.md"]) }),
      });
      renderDialog();
      await userEvent.type(await screen.findByLabelText("How many links deep"), "{backspace}1");
      const publish = await screen.findByRole("button", { name: "Publish 2 notes" });
      await vi.waitFor(() => expect(publish).toBeEnabled());
      await userEvent.click(publish);
      expect(await screen.findByText("Something changed in the vault: check the list again.")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Publish 3 notes" })).toBeInTheDocument();
      expect(screen.queryByLabelText("Link")).not.toBeInTheDocument();
    });
  });
});
