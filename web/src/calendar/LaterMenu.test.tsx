// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { bodiesOf, entryView, openTask, writtenView } from "../test/calendar";
import { fakeApi, json } from "../test/app";
import type { EntryView } from "./api";
import LaterMenu from "./LaterMenu";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useEditor.getState().reset();
});

function show(entry: EntryView, onProblem = vi.fn()) {
  const fetch = fakeApi({ "POST /api/vaults/Work/calendar/later": () => json(200, writtenView(entry.path)) });
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <LaterMenu entry={entry} onProblem={onProblem} />
    </QueryClientProvider>,
  );
  return fetch;
}

const open = async (title: string) => {
  await userEvent.click(screen.getByRole("button", { name: `Later: ${title}` }));
  return screen.getByRole("menu");
};
const names = (within_: HTMLElement) => within(within_).getAllByRole("menuitem").map((b) => b.textContent);

describe("Later", () => {
  it("Later without until has no headings", async () => {
    const fetch = show(openTask());
    const menu = await open("Certificate");
    expect(names(menu)).toEqual(["+1 day", "+1 week", "Pick a date…"]);
    expect(within(menu).queryByRole("group")).not.toBeInTheDocument();
    expect(within(menu).queryByText("Postpone")).not.toBeInTheDocument();
    expect(within(menu).queryByText("More time")).not.toBeInTheDocument();

    await userEvent.click(within(menu).getByRole("menuitem", { name: "+1 day" }));
    await waitFor(async () =>
      expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/later")).toEqual([{ path: "/Work/Certificate.md", part: "postpone", step: "day" }]),
    );
  });

  it("Later with until offers Postpone and More time", async () => {
    const fetch = show(openTask("Report", { until: "2026-12-18", lastDay: "2026-12-18" }));
    const menu = await open("Report");
    const postpone = within(menu).getByRole("group", { name: "Postpone" });
    const more = within(menu).getByRole("group", { name: "More time" });
    expect(names(postpone)).toEqual(["+1 day", "+1 week", "Pick a date…"]);
    expect(names(more)).toEqual(["+1 day", "+1 week", "Pick a date…"]);

    await userEvent.click(within(more).getByRole("menuitem", { name: "+1 week" }));
    await waitFor(async () =>
      expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/later")).toEqual([{ path: "/Work/Report.md", part: "moreTime", step: "week" }]),
    );
  });

  it("a week entry has no +1 day", async () => {
    show(openTask("Review", { when: "2026-W51", firstDay: "2026-12-14", lastDay: "2026-12-20" }));
    expect(names(await open("Review"))).toEqual(["+1 week", "Pick a date…"]);
  });

  it("a card is postponed only, without headings", async () => {
    show(entryView("Release", { path: "/Work/Board/Release.md", kind: "card", column: "In progress", state: "upcoming", until: null }));
    const menu = await open("Release");
    expect(names(menu)).toEqual(["+1 day", "+1 week", "Pick a date…"]);
    expect(within(menu).queryByRole("group")).not.toBeInTheDocument();
  });

  it("Pick a date… asks for the day and moves the to-do there", async () => {
    const fetch = show(openTask("Report", { until: "2026-12-18", lastDay: "2026-12-18" }));
    const menu = await open("Report");
    await userEvent.click(within(within(menu).getByRole("group", { name: "Postpone" })).getByRole("menuitem", { name: "Pick a date…" }));
    const dialog = screen.getByRole("dialog", { name: "Postpone Report" });
    await userEvent.type(within(dialog).getByLabelText("Day"), "2027-01-11");
    await userEvent.click(within(dialog).getByRole("button", { name: "Postpone" }));
    await waitFor(async () =>
      expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/calendar/later")).toEqual([{ path: "/Work/Report.md", part: "postpone", date: "2027-01-11" }]),
    );
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("says why when the server refuses", async () => {
    const onProblem = vi.fn();
    const entry = openTask();
    fakeApi({ "POST /api/vaults/Work/calendar/later": () => json(400, { error: "/Work/Certificate.md is closed; calendar reopen <note> opens it again" }) });
    render(
      <QueryClientProvider client={new QueryClient()}>
        <LaterMenu entry={entry} onProblem={onProblem} />
      </QueryClientProvider>,
    );
    await userEvent.click(within(await open("Certificate")).getByRole("menuitem", { name: "+1 week" }));
    await waitFor(() => expect(onProblem).toHaveBeenLastCalledWith("/Work/Certificate.md is closed; calendar reopen <note> opens it again"));
  });
});
