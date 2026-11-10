// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json, noContent, user } from "../test/app";
import PublicationBar from "./PublicationBar";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const NOTE = "/Work/Questions.md";
const API = "/api/publications/Work/Questions.md";
// Made up for the test, as the server would make it: the code in the fragment.
const link = "https://kumbu.example.org/s#ABCDEFGHJKMNPQRSTVWXYZ0123456789";
// Local times, so the bar shows them as written whatever the test machine's time zone.
const snapshotAt = new Date("2026-11-05T14:32:00").toISOString();
const changedAt = new Date("2026-11-05T16:05:00").toISOString();

const current = { path: NOTE, snapshotAt, older: false, changedAt: null, publishedBy: "you", link };
const older = { ...current, older: true, changedAt };
const asMember = { path: NOTE, snapshotAt: null, older: null, changedAt: null, publishedBy: null, link: null };

const session = (publishEnabled = true) => () => json(200, { user, previousSignInAt: null, passwordWindowOpen: true, publishEnabled });

// The vault's list, which names the note's publication (without its link) or doesn't.
const LIST = "GET /api/vaults/Work/publications";
const listed =
  (named = () => true) =>
  () =>
    json(200, { publications: named() ? [{ path: NOTE, snapshotAt, older: false, changedAt: null, publishedBy: "you", link: null }] : [], unpublishable: [] });
const asked = (fetch: ReturnType<typeof fakeApi>, path: string) =>
  fetch.mock.calls.filter(([r]) => (r as Request).method === "GET" && new URL((r as Request).url).pathname === path).length;

function renderBar(owner = true) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { container } = render(
    <QueryClientProvider client={queryClient}>
      <PublicationBar path={NOTE} owner={owner} />
    </QueryClientProvider>,
  );
  return container;
}

const bar = () => screen.findByRole("region", { name: "Publication" });

function stubClipboard() {
  const writeText = vi.fn().mockResolvedValue(undefined);
  vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
  return writeText;
}

describe("PublicationBar", () => {
  it("is green when up to date", async () => {
    const writeText = stubClipboard();
    fakeApi({ "GET /api/session": session(), [LIST]: listed(), [`GET ${API}`]: () => json(200, current) });
    renderBar();
    const region = await bar();
    expect(region).toHaveAttribute("data-tone", "green");
    expect(within(region).getByText("Published, up to date")).toBeInTheDocument();
    expect(within(region).queryByRole("button", { name: "Update copy" })).not.toBeInTheDocument();
    expect(within(region).getByRole("button", { name: "Unpublish" })).toBeInTheDocument();
    await userEvent.click(within(region).getByRole("button", { name: "Copy link" }));
    expect(writeText).toHaveBeenCalledWith(link);
  });

  it("is yellow with both times when older", async () => {
    fakeApi({ "GET /api/session": session(), [LIST]: listed(), [`GET ${API}`]: () => json(200, older) });
    renderBar();
    const region = await bar();
    expect(region).toHaveAttribute("data-tone", "yellow");
    expect(within(region).getByText("Published copy is older than this note. Published 5 Nov, 14:32 · changed 16:05")).toBeInTheDocument();
    for (const name of ["Update copy", "Copy link", "Unpublish"]) expect(within(region).getByRole("button", { name })).toBeInTheDocument();
  });

  it("updates the copy and keeps the link", async () => {
    const writeText = stubClipboard();
    let updated = false;
    const put = vi.fn(() => {
      updated = true;
      return json(200, { link, snapshotAt: changedAt, plain: [] });
    });
    fakeApi({
      "GET /api/session": session(),
      [`GET ${API}`]: () => json(200, updated ? { ...current, snapshotAt: changedAt } : older),
      [`PUT ${API}`]: put,
      "GET /api/publications": () => json(200, []),
      [LIST]: listed(),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    });
    renderBar();
    await userEvent.click(within(await bar()).getByRole("button", { name: "Update copy" }));
    expect(put).toHaveBeenCalledTimes(1);
    expect(await screen.findByText("Published, up to date")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Copy link" }));
    expect(writeText).toHaveBeenCalledWith(link);
  });

  it("keeps the link in no cache once the bar is gone, nor after an update", async () => {
    fakeApi({
      "GET /api/session": session(),
      [`GET ${API}`]: () => json(200, older),
      [`PUT ${API}`]: () => json(200, { link, snapshotAt: changedAt, plain: [] }),
      "GET /api/publications": () => json(200, []),
      [LIST]: listed(),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { unmount } = render(
      <QueryClientProvider client={queryClient}>
        <PublicationBar path={NOTE} owner />
      </QueryClientProvider>,
    );
    await userEvent.click(within(await bar()).getByRole("button", { name: "Update copy" }));
    await waitFor(() => expect(queryClient.getMutationCache().getAll()).toHaveLength(0));
    expect(queryClient.getQueryData(["publication", NOTE])).toBeDefined();
    unmount();
    await waitFor(() => expect(queryClient.getQueryCache().find({ queryKey: ["publication", NOTE] })).toBeUndefined());
  });

  it("reads older, never up to date, also without the times", async () => {
    fakeApi({ "GET /api/session": session(), [LIST]: listed(), [`GET ${API}`]: () => json(200, { ...older, snapshotAt: null, changedAt: null }) });
    renderBar();
    const region = await bar();
    expect(region).toHaveAttribute("data-tone", "yellow");
    expect(region).toHaveTextContent("Published copy is older than this note.");
    expect(region).not.toHaveTextContent("up to date");
    expect(region.textContent).not.toMatch(/\d/);
  });

  it("shows the green of its own, and says a failure as a status", async () => {
    fakeApi({
      "GET /api/session": session(),
      [`GET ${API}`]: () => json(200, current),
      [`DELETE ${API}`]: () => json(409, { error: "Work/Questions.md changed meanwhile." }),
      "GET /api/publications": () => json(200, []),
      [LIST]: listed(),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    });
    renderBar();
    const region = await bar();
    expect(region.querySelector("[aria-hidden]")).toHaveClass("bg-success");
    await userEvent.click(within(region).getByRole("button", { name: "Unpublish" }));
    const ask = screen.getByRole("alertdialog");
    expect(ask).toHaveAccessibleName("Withdraw the link? Whoever has it can't read the note any more.");
    await userEvent.click(within(ask).getByRole("button", { name: "Unpublish" }));
    expect((await within(region).findByText("Work/Questions.md changed meanwhile.")).closest("[role=status]")).toBeInTheDocument();
  });

  it("is neutral for a member", async () => {
    fakeApi({ "GET /api/session": session(), [LIST]: listed(), [`GET ${API}`]: () => json(200, asMember) });
    renderBar(false);
    const region = await bar();
    expect(region).toHaveTextContent(/^Published by the vault's owner$/);
    expect(region).not.toHaveAttribute("data-tone");
    expect(within(region).queryByRole("button")).not.toBeInTheDocument();
    expect(region.textContent).not.toMatch(/\d/);
  });

  it("shows a member no times even if the server sent them", async () => {
    fakeApi({ "GET /api/session": session(), [LIST]: listed(), [`GET ${API}`]: () => json(200, older) });
    renderBar(false);
    const region = await bar();
    expect(region).toHaveTextContent(/^Published by the vault's owner$/);
    expect(within(region).queryByRole("button")).not.toBeInTheDocument();
  });

  it("asks for no publication of its own for an unpublished note", async () => {
    for (const owner of [true, false]) {
      const fetch = fakeApi({ "GET /api/session": session(), [LIST]: listed(() => false), [`GET ${API}`]: () => json(404, { error: "Work/Questions.md is not published." }) });
      const container = renderBar(owner);
      await waitFor(() => expect(asked(fetch, "/api/vaults/Work/publications")).toBe(1));
      await new Promise((r) => setTimeout(r, 50));
      expect(asked(fetch, API)).toBe(0);
      expect(container).toBeEmptyDOMElement();
      cleanup();
    }
  });

  it("shows no bar and asks nothing of the note while the vault's list hasn't answered", async () => {
    const fetch = fakeApi({ "GET /api/session": session(), [LIST]: () => new Promise<Response>(() => {}), [`GET ${API}`]: () => json(200, current) });
    const container = renderBar();
    await waitFor(() => expect(asked(fetch, "/api/vaults/Work/publications")).toBe(1));
    await new Promise((r) => setTimeout(r, 50));
    expect(asked(fetch, API)).toBe(0);
    expect(container).toBeEmptyDOMElement();
  });

  it("takes a published note's bar from its own read once the vault's list names it", async () => {
    const fetch = fakeApi({ "GET /api/session": session(), [LIST]: listed(), [`GET ${API}`]: () => json(200, older) });
    renderBar();
    const region = await bar();
    for (const name of ["Update copy", "Copy link", "Unpublish"]) expect(within(region).getByRole("button", { name })).toBeInTheDocument();
    expect(asked(fetch, API)).toBe(1);
  });

  it("gives a member the bar of a note the shared vault's list names", async () => {
    const fetch = fakeApi({ "GET /api/session": session(), [LIST]: listed(), [`GET ${API}`]: () => json(200, asMember) });
    renderBar(false);
    expect(await bar()).toHaveTextContent(/^Published by the vault's owner$/);
    expect(asked(fetch, "/api/vaults/Work/publications")).toBe(1);
  });

  it("still offers Unpublish for a published note while switched off", async () => {
    fakeApi({ "GET /api/session": session(false), [LIST]: listed(), [`GET ${API}`]: () => json(200, current) });
    renderBar();
    const region = await bar();
    await waitFor(() => expect(within(region).getAllByRole("button").map((b) => b.textContent)).toEqual(["Unpublish"]));
  });

  it("asks before unpublishing", async () => {
    let withdrawn = false;
    const del = vi.fn(() => {
      withdrawn = true;
      return noContent();
    });
    fakeApi({
      "GET /api/session": session(),
      [`GET ${API}`]: () => (withdrawn ? json(404, { error: "Work/Questions.md is not published." }) : json(200, current)),
      [`DELETE ${API}`]: del,
      "GET /api/publications": () => json(200, []),
      [LIST]: listed(() => !withdrawn),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    });
    renderBar();
    await userEvent.click(within(await bar()).getByRole("button", { name: "Unpublish" }));
    const ask = screen.getByRole("alertdialog");
    expect(within(ask).getByText("Withdraw the link? Whoever has it can't read the note any more.")).toBeInTheDocument();
    await userEvent.click(within(ask).getByRole("button", { name: "Keep" }));
    expect(del).not.toHaveBeenCalled();
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();

    await userEvent.click(within(await bar()).getByRole("button", { name: "Unpublish" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Unpublish" }));
    expect(del).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.queryByRole("region", { name: "Publication" })).not.toBeInTheDocument());
  });

  it("offers only Unpublish while switched off", async () => {
    const del = vi.fn(() => noContent());
    fakeApi({
      "GET /api/session": session(false),
      [`GET ${API}`]: () => json(200, older),
      [`DELETE ${API}`]: del,
      "GET /api/publications": () => json(200, []),
      [LIST]: listed(),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
    });
    renderBar();
    const region = await bar();
    await waitFor(() =>
      expect(within(region).getByText("Published. Publishing is switched off on this server, so the link reads nothing until it is on again.")).toBeInTheDocument(),
    );
    expect(within(region).queryByRole("button", { name: "Update copy" })).not.toBeInTheDocument();
    expect(within(region).queryByRole("button", { name: "Copy link" })).not.toBeInTheDocument();
    expect(within(region).getAllByRole("button").map((b) => b.textContent)).toEqual(["Unpublish"]);
    await userEvent.click(within(region).getByRole("button", { name: "Unpublish" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Unpublish" }));
    expect(del).toHaveBeenCalledTimes(1);
  });
});
