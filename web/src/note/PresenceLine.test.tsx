// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, render, renderHook, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { presenceKey, useSyncStatus } from "../state/sync";
import { fakeApi, json, renderApp, user } from "../test/app";
import { FakeSocket } from "../test/socket";
import PresenceLine, { EDITING_REFRESH, presenceText, useEditingPresence } from "./PresenceLine";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
  useSyncStatus.setState({ connected: false, editors: {}, send: () => false });
});

const editing = (path: string, editors: string[]) => act(() => useSyncStatus.setState({ editors: { [presenceKey("Work", path)]: editors } }));

describe("the presence line", () => {
  it("shows 'Bob, Carol and 2 other users are editing this note'", () => {
    render(<PresenceLine path="/Work/plan.md" />);
    expect(screen.queryByRole("status", { name: "Editing now" })).not.toBeInTheDocument();

    editing("plan.md", ["Bob", "Carol", "2 other users"]);
    expect(screen.getByRole("status", { name: "Editing now" })).toHaveTextContent("Bob, Carol and 2 other users are editing this note");

    // Another note's editors are not this one's.
    editing("other.md", ["Bob"]);
    expect(screen.queryByRole("status", { name: "Editing now" })).not.toBeInTheDocument();
  });

  it("says it of one editor, of the owner by her quoted name, of others only counted, and of the user's other tab", () => {
    expect(presenceText([])).toBeNull();
    expect(presenceText(["Bob"])).toBe("Bob is editing this note");
    expect(presenceText(['"Alice"', "another user"])).toBe('"Alice" and another user are editing this note');
    expect(presenceText(["another user"])).toBe("Another user is editing this note");
    expect(presenceText(["3 other users"])).toBe("3 other users are editing this note");
    expect(presenceText(["You"])).toBe("You are editing this note in another tab");
  });

  it("sends editing on/off and refreshes every 30 s", () => {
    vi.useFakeTimers();
    const sent: unknown[] = [];
    useSyncStatus.setState({ connected: true, send: (frame) => sent.push(frame) > 0 });
    const on = { type: "editing", vault: "Work", path: "projects/plan.md", on: true };

    const hook = renderHook(({ active }) => useEditingPresence("/Work/projects/plan.md", active), { initialProps: { active: true } });
    expect(sent).toEqual([on]);
    act(() => vi.advanceTimersByTime(EDITING_REFRESH - 1));
    expect(sent).toHaveLength(1);
    act(() => vi.advanceTimersByTime(1));
    expect(sent).toEqual([on, on]);

    // Leaving Write mode says so, and nothing more is sent.
    hook.rerender({ active: false });
    expect(sent).toEqual([on, on, { ...on, on: false }]);
    act(() => vi.advanceTimersByTime(EDITING_REFRESH * 3));
    expect(sent).toHaveLength(3);
  });

  it("says it again after a reconnect, whose server knows nothing of it", () => {
    const sent: unknown[] = [];
    useSyncStatus.setState({ connected: true, send: (frame) => sent.push(frame) > 0 });
    renderHook(() => useEditingPresence("/Work/plan.md", true));
    act(() => useSyncStatus.setState({ connected: false, send: () => false }));
    act(() => useSyncStatus.setState({ connected: true, send: (frame) => sent.push(frame) > 0 }));
    expect(sent.filter((f) => (f as { on: boolean }).on)).toHaveLength(2);
  });

  it("is shown above the note in Write mode from the socket's presence, and not in Read mode", async () => {
    FakeSocket.all = [];
    vi.stubGlobal("WebSocket", FakeSocket);
    const tree = { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [{ name: "plan.md", path: "/Work/plan.md", size: 10 }] } };
    fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null }),
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal" }]),
      "GET /api/vaults/Work/tree": () => json(200, tree),
      "GET /api/notes/Work/plan.md": () => json(200, { path: "/Work/plan.md", content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z", current: true }),
      "GET /api/version/Work/plan.md": () => json(200, { path: "/Work/plan.md", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
      "GET /api/links/*": () => json(200, []),
      "GET /api/backlinks/*": () => json(200, []),
      "GET /api/changes": () => json(200, []),
      "GET /api/member-notices": () => json(200, []),
      "POST /api/ws-ticket": () => json(200, { ticket: "t1", expiresAt: "2026-11-01T10:00:30Z" }),
    });
    renderApp("/notes/Work/plan.md");
    await screen.findByText("Text.");
    await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
    const socket = FakeSocket.all[0];
    socket.open();
    socket.frame({ type: "presence", vault: "Work", path: "plan.md", editors: ['"Alice"', "another user"] });
    expect(screen.queryByRole("status", { name: "Editing now" })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Write" }));
    expect(await screen.findByRole("status", { name: "Editing now" })).toHaveTextContent('"Alice" and another user are editing this note');
    expect(socket.sent).toContainEqual({ type: "editing", vault: "Work", path: "plan.md", on: true });

    await userEvent.click(screen.getByRole("button", { name: "Read" }));
    await waitFor(() => expect(socket.sent).toContainEqual({ type: "editing", vault: "Work", path: "plan.md", on: false }));
    expect(screen.queryByRole("status", { name: "Editing now" })).not.toBeInTheDocument();
  });
});
