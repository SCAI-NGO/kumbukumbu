// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { components } from "../api/schema";
import { timeText } from "../components/Sheet";
import { useDiscreet } from "../discreet/state";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { FakeSocket } from "../test/socket";

type BlameView = components["schemas"]["BlameView"];

let desktop = true;
beforeEach(() => {
  desktop = true;
  FakeSocket.all = [];
  vi.stubGlobal("WebSocket", FakeSocket);
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: desktop && query === "(min-width: 64rem)", media: query, addEventListener() {}, removeEventListener() {} }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  localStorage.clear();
  sessionStorage.clear();
});

const hoursAgo = (hours: number) => new Date(Date.now() - hours * 3_600_000).toISOString();
const brita = hoursAgo(2);
const veyalt = hoursAgo(5);
const EARLIER = "2026-11-17T18:00:00Z";
const day = (iso: string) => new Date(iso).toLocaleDateString("en-GB", { day: "numeric", month: "short" });
const dayAndTime = (iso: string) => `${day(iso)} ${new Date(iso).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" })}`;

const CONTENT = [
  "---",
  "status: draft",
  "---",
  "Intro by Veyalt.",
  "",
  "# Results",
  "",
  "Brita wrote this.",
  "Veyalt fixed this.",
  "",
  "## Notes",
  "",
  "Old text.",
  "",
].join("\n");

const protocol: BlameView = {
  path: "/Work/protocol.md",
  version: "v3",
  content: CONTENT,
  earlier: EARLIER,
  dropped: false,
  writers: [
    { display: "Brita", kind: null },
    { display: "Veyalt", kind: "device" },
  ],
  sections: [
    { start: 1, end: 3, title: "Properties", writer: 0, others: false, counts: [{ writer: 0, display: "Brita", lines: 3, at: brita }] },
    { start: 4, end: 5, title: "Top", writer: 1, others: false, counts: [{ writer: 1, display: "Veyalt", lines: 1, at: veyalt }] },
    {
      start: 6,
      end: 10,
      title: "Results",
      writer: 0,
      others: true,
      counts: [
        { writer: 0, display: "Brita", lines: 2, at: brita },
        { writer: 1, display: "Veyalt (mv)", lines: 1, at: veyalt },
      ],
    },
    { start: 11, end: 13, title: "Notes", writer: null, others: false, counts: [{ writer: null, display: "earlier", lines: 2, at: EARLIER }] },
  ],
};

const vault = (name: string) => ({ name, notes: 4, embeddings: "internal", canWrite: true, level: "owner" });

/** The app's API, with `/api/blame` answering `answer` and the vault list holding `at.names`. */
function api(answer: () => Response | Promise<Response>, at = { names: ["Work"] }) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, at.names.map(vault)),
    "POST /api/ws-ticket": () => json(200, { ticket: "t1", expiresAt: "2026-11-17T10:00:30Z" }),
    "GET /api/calendar/whats-on": () => json(200, { items: [], count: 0, overdue: false, vaults: [], couldntRead: 0, couldntReadVaults: [] }),
    "GET /api/blame/*": answer,
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/*": (r) => {
      const [, , , name, what] = new URL(r.url).pathname.split("/").map(decodeURIComponent);
      if (what === "publications") return json(200, { publications: [], unpublishable: [] });
      return json(200, { damaged: 0, root: { name, path: `/${name}`, folders: [], notes: [] } });
    },
    "GET /api/notes/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "Bob", lastWriteAt: hoursAgo(1) }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
  });
}

const page = () => screen.findByRole("dialog", { name: "Who wrote what" });
const sections = () => within(screen.getByRole("list", { name: "Sections" })).getAllByRole("listitem").filter((li) => li.dataset.section !== undefined);
const gutter = (section: HTMLElement) => within(section).getAllByRole("button")[0];

describe("Who wrote what", () => {
  it("asks for the note of its address", async () => {
    const fetch = api(() => json(200, protocol));
    renderApp("/blame/Work/Protocols/protocol%20one.md");
    await page();
    await waitFor(() => expect(fetch.mock.calls.map(([r]) => new URL((r as Request).url).pathname)).toContain("/api/blame/Work/Protocols/protocol%20one.md"));
  });

  it("the legend counts sections by their main writer, and says why some lines are older", async () => {
    api(() => json(200, protocol));
    renderApp("/blame/Work/protocol.md");
    await page();
    const legend = await screen.findByRole("list", { name: "Writers" });
    expect(within(legend).getAllByRole("listitem").map((li) => li.textContent)).toEqual([
      "2 sections by Brita",
      "1 by Veyalt · Obsidian device",
      `1 from before ${day(EARLIER)}`,
    ]);
    expect(screen.getByText(`Kumbu² records who wrote each line from version 0.6.0 on; lines from before ${dayAndTime(EARLIER)} are older than what the note's history keeps.`)).toBeInTheDocument();
    // The note's title heads it.
    expect(screen.getByRole("heading", { name: "protocol" })).toBeInTheDocument();
  });

  it("each section in note order, its gutter naming the writer, the kind and the newest time", async () => {
    api(() => json(200, protocol));
    renderApp("/blame/Work/protocol.md");
    await page();
    await waitFor(() => expect(sections()).toHaveLength(4));
    const [properties, top, results, notes] = sections();

    expect(gutter(properties)).toHaveTextContent("Brita");
    expect(gutter(properties)).not.toHaveTextContent("others");
    expect(within(properties).getByText("draft")).toBeInTheDocument();

    expect(gutter(top)).toHaveTextContent("Veyalt");
    expect(gutter(top)).toHaveTextContent("Obsidian device");
    expect(gutter(top)).toHaveTextContent("5 hours ago");
    expect(within(top).getByText("Intro by Veyalt.")).toBeInTheDocument();

    expect(gutter(results)).toHaveTextContent("Brita and others");
    expect(gutter(results)).toHaveTextContent("2 hours ago");
    // The full time on hover and for screen readers.
    expect(within(gutter(results)).getByTitle(timeText(brita))).toBeInTheDocument();
    expect(within(gutter(results)).getByText(timeText(brita))).toHaveClass("sr-only");
    expect(within(results).getByRole("heading", { name: "Results" })).toBeInTheDocument();
    expect(within(results).getByText(/Veyalt fixed this\./)).toBeInTheDocument();
    expect(within(results).queryByText("Old text.")).not.toBeInTheDocument();

    expect(gutter(notes)).toHaveTextContent(`Before ${day(EARLIER)}`);
    expect(within(notes).getByRole("heading", { name: "Notes" })).toBeInTheDocument();
    expect(within(notes).getByText("Old text.")).toBeInTheDocument();
  });

  it("activating the gutter shows every writer of the section, mark kept, in place", async () => {
    api(() => json(200, protocol));
    renderApp("/blame/Work/protocol.md");
    await page();
    await waitFor(() => expect(sections()).toHaveLength(4));
    const results = sections()[2];
    const button = gutter(results);
    expect(button).toHaveAttribute("aria-expanded", "false");
    expect(within(results).queryByRole("list", { name: "Writers of Results" })).not.toBeInTheDocument();

    button.focus();
    await userEvent.keyboard("{Enter}");
    expect(button).toHaveAttribute("aria-expanded", "true");
    const detail = within(results).getByRole("list", { name: "Writers of Results" });
    const rows = within(detail).getAllByRole("listitem");
    expect(rows[0]).toHaveTextContent(/^Brita, 2 lines, /);
    expect(rows[1]).toHaveTextContent(/^Veyalt \(mv\) · Obsidian device, 1 line, /);
    expect(within(rows[1]).getByTitle(timeText(veyalt))).toBeInTheDocument();
    expect(button).toHaveAttribute("aria-controls", detail.id);

    await userEvent.click(button);
    expect(button).toHaveAttribute("aria-expanded", "false");
    expect(within(results).queryByRole("list", { name: "Writers of Results" })).not.toBeInTheDocument();
  });

  it("the lines before the record in the detail", async () => {
    api(() => json(200, protocol));
    renderApp("/blame/Work/protocol.md");
    await page();
    await waitFor(() => expect(sections()).toHaveLength(4));
    const notes = sections()[3];
    await userEvent.click(gutter(notes));
    expect(within(within(notes).getByRole("list", { name: "Writers of Notes" })).getByRole("listitem")).toHaveTextContent(`Before ${dayAndTime(EARLIER)}, 2 lines`);
  });

  it("names every writer, never colour alone", async () => {
    api(() => json(200, protocol));
    renderApp("/blame/Work/protocol.md");
    await page();
    await waitFor(() => expect(sections()).toHaveLength(4));
    for (const section of sections()) expect(gutter(section).textContent).toMatch(/Brita|Veyalt|Before/);
    // The marks are decoration beside the names.
    for (const mark of document.querySelectorAll("[data-mark]")) expect(mark).toHaveAttribute("aria-hidden", "true");
  });

  it("on the phone the gutter sits above the section", async () => {
    desktop = false;
    api(() => json(200, protocol));
    renderApp("/blame/Work/protocol.md");
    await page();
    await waitFor(() => expect(sections()).toHaveLength(4));
    expect(sections()[0]).toHaveClass("flex-col");
    expect(sections()[0]).not.toHaveClass("grid");
    // The bar names the note; the body doesn't again.
    expect(screen.getByRole("dialog", { name: "Who wrote what" })).toHaveTextContent("· protocol");
    expect(screen.queryByRole("heading", { name: "protocol" })).not.toBeInTheDocument();
  });

  it("beside the section on the desktop", async () => {
    api(() => json(200, protocol));
    renderApp("/blame/Work/protocol.md");
    await page();
    await waitFor(() => expect(sections()).toHaveLength(4));
    expect(sections()[0]).toHaveClass("grid");
  });

  it("every line recorded: no sentence, no earlier count", async () => {
    api(() => json(200, { ...protocol, earlier: null, sections: protocol.sections.slice(0, 3) }));
    renderApp("/blame/Work/protocol.md");
    await page();
    const legend = await screen.findByRole("list", { name: "Writers" });
    expect(within(legend).getAllByRole("listitem").map((li) => li.textContent)).toEqual(["2 sections by Brita", "1 by Veyalt · Obsidian device"]);
    expect(screen.queryByText(/records who wrote each line/)).not.toBeInTheDocument();
  });

  it("one section reads in the singular", async () => {
    api(() => json(200, { ...protocol, earlier: null, sections: protocol.sections.slice(1, 2) }));
    renderApp("/blame/Work/protocol.md");
    await page();
    const legend = await screen.findByRole("list", { name: "Writers" });
    expect(within(legend).getByRole("listitem")).toHaveTextContent("1 section by Veyalt · Obsidian device");
  });

  it("a record that started again says so", async () => {
    api(() => json(200, { ...protocol, dropped: true }));
    renderApp("/blame/Work/protocol.md");
    await page();
    expect(
      await screen.findByText(`The record of who wrote each line started again at ${dayAndTime(EARLIER)}, after more than 4,096 changes of writer; lines from before are older than that.`),
    ).toBeInTheDocument();
    expect(screen.queryByText(/from version 0\.6\.0 on/)).not.toBeInTheDocument();
  });

  it("an empty note", async () => {
    api(() => json(200, { ...protocol, content: "", earlier: null, writers: [], sections: [] }));
    renderApp("/blame/Work/protocol.md");
    await page();
    expect(await screen.findByText("This note is empty: no one has written in it yet.")).toBeInTheDocument();
    expect(screen.queryByRole("list", { name: "Writers" })).not.toBeInTheDocument();
  });

  it("loading, then the note", async () => {
    let answer!: (r: Response) => void;
    api(() => new Promise<Response>((resolve) => (answer = resolve)));
    renderApp("/blame/Work/protocol.md");
    await page();
    expect(await screen.findByText("Loading …")).toBeInTheDocument();
    await waitFor(() => expect(answer).toBeDefined());
    answer(json(200, protocol));
    await waitFor(() => expect(sections()).toHaveLength(4));
    expect(screen.queryByText("Loading …")).not.toBeInTheDocument();
  });

  it("closes, showing nothing of the note, when its vault leaves the vault list", async () => {
    const at = { names: ["Work", "Garten"] };
    api(() => json(200, protocol), at);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp(["/notes/Work/protocol.md", "/blame/Work/protocol.md"], queryClient);
    await page();
    expect(await screen.findByText(/Brita wrote this\./)).toBeInTheDocument();
    await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
    FakeSocket.all.at(-1)!.open();
    await waitFor(() => expect(queryClient.getQueryData<{ name: string }[]>(["vaults"])?.map((v) => v.name)).toEqual(["Work", "Garten"]));

    // The socket is closed for the vault, opens again, and the list no longer has Work.
    at.names = ["Garten"];
    FakeSocket.all.at(-1)!.drop(4412);
    await waitFor(() => expect(FakeSocket.all).toHaveLength(2));
    FakeSocket.all.at(-1)!.open();
    await waitFor(() => expect(queryClient.getQueryData<{ name: string }[]>(["vaults"])?.map((v) => v.name)).toEqual(["Garten"]));

    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Who wrote what" })).not.toBeInTheDocument());
    expect(screen.queryByText(/Brita wrote this\./)).not.toBeInTheDocument();
    expect(router.state.location.pathname).not.toMatch(/^\/blame\//);
    expect(window.location.pathname).not.toContain("Work/protocol");
  });

  it("shows only the error when the note is read again and refused", async () => {
    let refused = false;
    api(() => (refused ? json(400, { error: "/Work/protocol.md can't be read now." }) : json(200, protocol)));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp("/blame/Work/protocol.md", queryClient);
    await page();
    expect(await screen.findByText(/Brita wrote this\./)).toBeInTheDocument();
    refused = true;
    await act(() => queryClient.refetchQueries({ predicate: (q) => q.queryKey[0] === "blame" }));
    await waitFor(() => expect(screen.queryByText("Loading …")).not.toBeInTheDocument());
    expect(await screen.findByText("/Work/protocol.md can't be read now.")).toBeInTheDocument();
    expect(screen.queryByText(/Brita wrote this\./)).not.toBeInTheDocument();
  });

  it("an error says what went wrong", async () => {
    api(() => json(400, { error: "/Work/scan.pdf is an attachment: only notes have lines." }));
    renderApp("/blame/Work/scan.pdf");
    await page();
    expect(await screen.findByText("/Work/scan.pdf is an attachment: only notes have lines.")).toBeInTheDocument();
  });
});
