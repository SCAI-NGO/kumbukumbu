// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });
const tree = {
  damaged: 0,
  root: { name: "Work", path: "/Work", folders: [], notes: [note("/Work/plan.md"), note("/Work/roadmap.md")] },
};
const content = ["# Plan", "", "Why we do it.", "", "## Goals", "", "- one", "", "## Risks", "", "None yet."].join("\n");
const storageKey = "kumbu.sidePanelCollapsed";

/** The window's width: wide (from 1280 px), desktop (from 1024 px) or a phone's. */
let width: "wide" | "desktop" | "phone" = "wide";

function signedIn() {
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    "GET /api/notes/*": () => json(200, { path: "/Work/plan.md", content, version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "PUT /api/notes/*": () => json(200, { path: "/Work/plan.md", version: "v2", lastWriteAt: "2026-10-25T09:01:00Z" }),
    "GET /api/version/*": () => json(200, { path: "/Work/plan.md", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, [{ path: "/Work/roadmap.md", line: 3, text: "See [[plan]]", ambiguous: false }]),
    "GET /api/changes": () => json(200, []),
  });
}

const panel = () => screen.getByRole("complementary", { name: "Side panel" });
const toggle = () => screen.getByRole("button", { name: "Side panel" });

async function open() {
  signedIn();
  renderApp("/notes/Work/plan.md");
  await screen.findByRole("heading", { name: "Plan" });
}

beforeEach(() => {
  width = "wide";
  localStorage.clear();
  vi.stubGlobal("matchMedia", (query: string) => ({
    get matches() {
      if (query === "(min-width: 80rem)") return width === "wide";
      if (query === "(min-width: 64rem)") return width !== "phone";
      return false;
    },
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  }));
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

describe("the side panel in the shell", () => {
  it("sits beside the note on a wide window, with Linked from in it and not at the note's bottom", async () => {
    await open();
    const linked = await within(panel()).findByRole("region", { name: "Linked from" });
    expect(linked).toHaveTextContent("roadmap");
    expect(screen.getAllByRole("region", { name: "Linked from" })).toHaveLength(1);
    expect(within(screen.getByRole("main")).queryByRole("region", { name: "Unlinked mentions" })).not.toBeInTheDocument();
    expect(within(screen.getByRole("main")).queryByRole("complementary", { name: "Side panel" })).not.toBeInTheDocument();
  });

  it("has its toggle at the top bar's right end, and the top bar no Outline button", async () => {
    await open();
    const buttons = within(screen.getByRole("banner")).getAllByRole("button");
    expect(buttons.at(-1)).toBe(toggle());
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    expect(toggle()).toHaveAttribute("aria-controls", panel().id);
    expect(toggle()).toHaveAttribute("aria-keyshortcuts", "Control+Shift+\\ Meta+Shift+\\");
    expect(toggle()).toHaveAttribute("title", "Hide the side panel (Ctrl+Shift+\\ or ⌘⇧\\)");
    expect(screen.queryByRole("button", { name: "Outline" })).not.toBeInTheDocument();
  });

  it("collapses and expands with its toggle, remembers it, and keeps Linked from off the note's bottom while collapsed", async () => {
    await open();
    await within(panel()).findByRole("region", { name: "Linked from" });
    await userEvent.click(toggle());
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    expect(toggle()).toHaveAttribute("title", "Show the side panel (Ctrl+Shift+\\ or ⌘⇧\\)");
    expect(screen.queryByRole("complementary", { name: "Side panel" })).not.toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "Linked from" })).not.toBeInTheDocument();
    expect(localStorage.getItem(storageKey)).toBe("true");
    cleanup();

    await open();
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    await userEvent.click(toggle());
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    expect(panel()).toBeVisible();
  });

  it("isn't there without a note", async () => {
    signedIn();
    renderApp("/");
    await screen.findByRole("heading", { name: "Work" });
    expect(screen.queryByRole("complementary", { name: "Side panel" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Side panel" })).not.toBeInTheDocument();
  });

  it.each(["desktop", "phone"] as const)("is not there on a %s, where Linked from stays at the note's bottom and the outline a sheet", async (w) => {
    width = w;
    await open();
    expect(await within(screen.getByRole("main")).findByRole("region", { name: "Linked from" })).toHaveTextContent("roadmap");
    expect(screen.queryByRole("complementary", { name: "Side panel" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Side panel" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Outline" }));
    expect(screen.getByRole("dialog", { name: "Outline" })).toBeInTheDocument();
  });

  it("jumps in Read mode to the heading clicked in its outline", async () => {
    await open();
    await userEvent.click(within(panel()).getByRole("tab", { name: "Outline" }));
    const risks = screen.getByRole("heading", { name: "Risks" });
    const scrolled = vi.fn();
    risks.scrollIntoView = scrolled;
    await userEvent.click(within(panel()).getByRole("button", { name: "Risks" }));
    await waitFor(() => expect(scrolled).toHaveBeenCalledWith({ block: "start" }));
  });
});
