// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import SidePanel from "./SidePanel";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const PLAN = "/Work/projects/plan.md";
const text = ["# Plan", "", "Why we do it.", "## Goals", "", "- one", "## Risks", "", "None yet."].join("\n");

const roadmap = [{ path: "/Work/projects/roadmap.md", line: 3, text: "See [[plan]]", ambiguous: false }];

function show(props: Partial<Parameters<typeof SidePanel>[0]> = {}, backlinks = roadmap) {
  fakeApi({ "GET /api/backlinks/*": () => json(200, backlinks) });
  const onJump = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter>
        <SidePanel path={PLAN} text={text} currentLine={1} onJump={onJump} {...props} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return onJump;
}

const outlineTab = () => userEvent.click(screen.getByRole("tab", { name: "Outline" }));

describe("side panel", () => {
  it("has the tabs Links and Outline, Links first and chosen", () => {
    show();
    const tabs = within(screen.getByRole("tablist")).getAllByRole("tab");
    expect(tabs.map((t) => t.textContent)).toEqual(["Links", "Outline"]);
    expect(tabs[0]).toHaveAttribute("aria-selected", "true");
    expect(tabs[1]).toHaveAttribute("aria-selected", "false");
    expect(screen.getByRole("tabpanel")).toHaveAccessibleName("Links");
  });

  it("shows the note's backlinks and its unlinked mentions under Links", async () => {
    show();
    const panel = screen.getByRole("tabpanel", { name: "Links" });
    expect(await within(panel).findByRole("region", { name: "Linked from" })).toHaveTextContent("roadmap");
    expect(within(panel).getByRole("button", { name: "Unlinked mentions" })).toBeInTheDocument();
  });

  it("moves along the tabs with the arrow keys, the focus with it", async () => {
    show();
    screen.getByRole("tab", { name: "Links" }).focus();
    await userEvent.keyboard("{ArrowRight}");
    expect(screen.getByRole("tab", { name: "Outline" })).toHaveFocus();
    expect(screen.getByRole("tab", { name: "Outline" })).toHaveAttribute("aria-selected", "true");
    await userEvent.keyboard("{ArrowRight}");
    expect(screen.getByRole("tab", { name: "Links" })).toHaveFocus();
  });

  it("wraps round with ArrowLeft, and goes to the first and last tab with Home and End", async () => {
    show();
    const links = screen.getByRole("tab", { name: "Links" });
    const outlineTab = screen.getByRole("tab", { name: "Outline" });
    links.focus();
    await userEvent.keyboard("{ArrowLeft}");
    expect(outlineTab).toHaveFocus();
    expect(outlineTab).toHaveAttribute("aria-selected", "true");
    await userEvent.keyboard("{Home}");
    expect(links).toHaveFocus();
    expect(links).toHaveAttribute("aria-selected", "true");
    await userEvent.keyboard("{End}");
    expect(outlineTab).toHaveFocus();
    expect(outlineTab).toHaveAttribute("aria-selected", "true");
    expect(links).toHaveAttribute("aria-selected", "false");
  });

  it("is named by the panel alone: the tab list carries no name of its own", () => {
    show();
    expect(screen.getByRole("tablist")).not.toHaveAttribute("aria-label");
  });

  it("takes the gap and the rule above Linked from for the note's bottom only", async () => {
    show();
    const linked = await screen.findByRole("region", { name: "Linked from" });
    expect(linked).not.toHaveClass("mt-[30px]");
    expect(linked).not.toHaveClass("border-t");
  });

  it("takes the gap and the rule above the unlinked mentions for the note's bottom only, without links to the note", async () => {
    show({}, []);
    const mentions = screen.getByRole("region", { name: "Unlinked mentions" });
    expect(mentions).not.toHaveClass("mt-[30px]");
    expect(mentions).not.toHaveClass("border-t");
  });

  it("lists the headings of the text it is given under Outline", async () => {
    show();
    await outlineTab();
    const panel = screen.getByRole("tabpanel", { name: "Outline" });
    expect(within(panel).getAllByRole("button").map((b) => b.textContent)).toEqual(["Plan", "Goals", "Risks"]);
  });

  it("marks the heading the reader is in: the last one at or above the current line", async () => {
    show({ currentLine: 6 });
    await outlineTab();
    expect(screen.getByRole("button", { name: "Goals" })).toHaveAttribute("aria-current", "location");
    expect(screen.getByRole("button", { name: "Plan" })).not.toHaveAttribute("aria-current");
    expect(screen.getByRole("button", { name: "Risks" })).not.toHaveAttribute("aria-current");
  });

  it("marks the heading on the current line itself, and none above the first", async () => {
    show({ text: `Intro\n\n${text}`, currentLine: 1 });
    await outlineTab();
    expect(screen.getByRole("tabpanel", { name: "Outline" }).querySelector("[aria-current]")).toBeNull();
    cleanup();
    show({ currentLine: 7 });
    await outlineTab();
    expect(screen.getByRole("button", { name: "Risks" })).toHaveAttribute("aria-current", "location");
  });

  it("says so when the note has no headings", async () => {
    show({ text: "Just a line." });
    await outlineTab();
    expect(screen.getByRole("tabpanel", { name: "Outline" })).toHaveTextContent("This note has no headings.");
  });

  it("takes no front matter for a heading, and counts its lines", async () => {
    show({ text: "---\ntitle: Plan\n---\nNo headings here." });
    await outlineTab();
    expect(screen.getByRole("tabpanel", { name: "Outline" })).toHaveTextContent("This note has no headings.");
    cleanup();
    const onJump = show({ text: "---\ntitle: Plan\n---\n## Goals" });
    await outlineTab();
    await userEvent.click(screen.getByRole("button", { name: "Goals" }));
    expect(onJump).toHaveBeenCalledWith(4);
  });

  it("jumps to a heading's line when it is clicked", async () => {
    const onJump = show();
    await outlineTab();
    await userEvent.click(screen.getByRole("button", { name: "Risks" }));
    expect(onJump).toHaveBeenCalledWith(7);
  });

  it("opens on the tab last chosen", async () => {
    show();
    await outlineTab();
    cleanup();
    show();
    expect(screen.getByRole("tab", { name: "Outline" })).toHaveAttribute("aria-selected", "true");
  });
});
