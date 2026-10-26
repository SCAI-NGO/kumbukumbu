// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json } from "../test/app";
import CompareVersions from "./CompareVersions";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const PLAN = "/Work/plan.md";
const texts: Record<string, string> = { v1: "# Plan\nZebras first.\n", v2: "# Plan\nLions now.\n", now: "# Plan\nLions now.\nTigers later.\n" };
const view = (version: string, current: boolean) => ({ path: PLAN, content: texts[version], version, lastWriter: "liz", lastWriteAt: "2026-10-26T09:00:00Z", current });

function api({ gone = false } = {}) {
  return fakeApi({
    "GET /api/notes/Work/plan.md": (r) => {
      const version = new URL(r.url).searchParams.get("version");
      if (version) return json(200, view(version, false));
      return gone ? json(404, { error: "/Work/plan.md: No such note" }) : json(200, view("now", true));
    },
  });
}

const show = (props: Partial<Parameters<typeof CompareVersions>[0]> = {}) =>
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <CompareVersions path={PLAN} version="v2" before="v1" trashed={false} beforeLost={false} {...props} />
    </QueryClientProvider>,
  );

const versionsAsked = (fetch: ReturnType<typeof api>) =>
  fetch.mock.calls.map(([r]) => new URL((r as Request).url)).filter((u) => u.pathname === "/api/notes/Work/plan.md").map((u) => u.searchParams.get("version"));

describe("comparing two versions of a note", () => {
  it("compares_with_the_version_before_by_default", async () => {
    const fetch = api();
    show();

    expect(screen.getByRole("button", { name: "Against the version before" })).toHaveAttribute("aria-pressed", "true");
    const left = await screen.findByRole("list", { name: "Before" });
    const right = screen.getByRole("list", { name: "After this change" });
    expect(await within(left).findByText("Zebras first.")).toBeInTheDocument();
    expect(within(left).getByText("Zebras first.").closest("li")).toHaveAttribute("data-kind", "removed");
    expect(within(right).getByText("Lions now.").closest("li")).toHaveAttribute("data-kind", "added");
    expect(versionsAsked(fetch).sort()).toEqual(["v1", "v2"]);
  });

  it("switches_to_now", async () => {
    const fetch = api();
    show();

    await userEvent.click(screen.getByRole("button", { name: "Against now" }));

    const right = await screen.findByRole("list", { name: "Now" });
    expect(await within(right).findByText("Tigers later.")).toBeInTheDocument();
    expect(within(right).getByText("Tigers later.").closest("li")).toHaveAttribute("data-kind", "added");
    expect(versionsAsked(fetch)).toContain(null);
  });

  it("can start against now", async () => {
    api();
    show({ initial: "now" });

    expect(screen.getByRole("button", { name: "Against now" })).toHaveAttribute("aria-pressed", "true");
    expect(await within(await screen.findByRole("list", { name: "Now" })).findByText("Tigers later.")).toBeInTheDocument();
  });

  it("says_added_in_this_change_without_before", async () => {
    api();
    show({ before: null });

    expect(screen.getByText("Added in this change")).toBeInTheDocument();
    expect(await within(await screen.findByRole("list", { name: "After this change" })).findByText("Lions now.")).toBeInTheDocument();
  });

  it("says_earlier_versions_are_no_longer_kept", async () => {
    api();
    show({ before: null, beforeLost: true });

    expect(screen.getByText("Earlier versions are no longer kept")).toBeInTheDocument();
  });

  it("says the note is in the trash now, against now", async () => {
    const fetch = api({ gone: true });
    show({ trashed: true });

    await userEvent.click(screen.getByRole("button", { name: "Against now" }));

    expect(await screen.findByText("This note is in the trash now")).toBeInTheDocument();
    expect(versionsAsked(fetch)).not.toContain(null);
  });

  it("says so when a rename left the text as it was", async () => {
    api();
    show({ before: "v2" });

    expect(await screen.findByText("The text didn't change in this change.")).toBeInTheDocument();
  });
});
