// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { consoleCall } from "./api";
import { forgetEntries, listFolder } from "./entries";
import { useConsole } from "./store";

vi.mock("./api", () => ({ consoleCall: vi.fn() }));
const call = vi.mocked(consoleCall);

const answer = (text: string, isError = false) =>
  Promise.resolve({ parts: [{ type: "text", text, mediaType: null, data: null }], isError, version: null });

beforeEach(() => {
  vi.useFakeTimers();
  forgetEntries();
  call.mockReset();
  call.mockImplementation(() => answer("a.md\nsub/"));
});
afterEach(() => vi.useRealTimers());

describe("listFolder", () => {
  it("asks `ls` once per folder, vaults with a bare ls", async () => {
    expect(await listFolder("/Work")).toEqual(["a.md", "sub/"]);
    await listFolder("/Work");
    await listFolder("/");
    expect(call.mock.calls.map(([request]) => request)).toEqual([
      { call: "sh", command: "ls -a /Work" },
      { call: "sh", command: "ls" },
    ]);
  });

  it("answers parallel keystrokes with one request", async () => {
    await Promise.all([listFolder("/Work"), listFolder("/Work"), listFolder("/Work")]);
    expect(call).toHaveBeenCalledTimes(1);
  });

  it("quotes a folder with spaces for the shell", async () => {
    await listFolder("/My Vault/it's");
    await listFolder("/My Vault");
    expect(call.mock.calls.map(([request]) => request)).toEqual([
      { call: "sh", command: `ls "/My Vault/it's"` },
      { call: "sh", command: "ls -a '/My Vault'" },
    ]);
  });

  it("refreshes after forgetEntries and after ten seconds", async () => {
    await listFolder("/Work");
    forgetEntries();
    await listFolder("/Work");
    expect(call).toHaveBeenCalledTimes(2);
    vi.advanceTimersByTime(9_000);
    await listFolder("/Work");
    expect(call).toHaveBeenCalledTimes(2);
    vi.advanceTimersByTime(1_500);
    await listFolder("/Work");
    expect(call).toHaveBeenCalledTimes(3);
  });

  it("is cleared by the console's reset and by every answered command", async () => {
    await listFolder("/Work");
    useConsole.getState().reset();
    await listFolder("/Work");
    expect(call).toHaveBeenCalledTimes(2);
    useConsole.getState().add({ id: 1, cwd: "/", typed: "cd x", output: [] });
    await listFolder("/Work");
    expect(call).toHaveBeenCalledTimes(3);
  });

  it("drops an answer that was on its way when the console reset", async () => {
    let finish!: (view: Awaited<ReturnType<typeof answer>>) => void;
    call.mockImplementationOnce(() => new Promise((resolve) => (finish = resolve)));
    const pending = listFolder("/Secret");
    useConsole.getState().reset();
    finish(await answer("hidden.md"));
    expect(await pending).toBeNull();
  });

  it("drops an answer that was on its way when a command ran", async () => {
    let finish!: (view: Awaited<ReturnType<typeof answer>>) => void;
    call.mockImplementationOnce(() => new Promise((resolve) => (finish = resolve)));
    const pending = listFolder("/Work/x");
    useConsole.getState().add({ id: 1, cwd: "/", typed: "mkdir x", output: [] });
    finish(await answer("stale.md"));
    expect(await pending).toBeNull();
    await listFolder("/Work/x");
    expect(call).toHaveBeenCalledTimes(2);
  });

  it("keeps names that only look like notices, and drops real ones", async () => {
    call.mockImplementationOnce(() =>
      answer(["(draft).md", "(old)/", "(v)/ (shared by Bo, read)", "(1 note here doesn't open: changed or damaged outside Kumbu²; the server log has details)"].join("\n")),
    );
    expect(await listFolder("/Work")).toEqual(["(draft).md", "(old)/", "(v)/"]);
  });

  it("gives null where the path is a note or an attachment, not a folder", async () => {
    call.mockImplementationOnce(() => answer("/Work/plan.md"));
    expect(await listFolder("/Work/plan.md")).toBeNull();
    call.mockImplementationOnce(() => answer("/Work/shot.png  image 10×10 1 KB"));
    expect(await listFolder("/Work/shot.png")).toBeNull();
  });

  it("gives null for a failing or erroring ls, and doesn't ask again at once", async () => {
    call.mockImplementationOnce(() => Promise.reject(new Error("offline")));
    expect(await listFolder("/Work")).toBeNull();
    expect(await listFolder("/Work")).toBeNull();
    expect(call).toHaveBeenCalledTimes(1);
    call.mockImplementationOnce(() => answer("ls: cannot access", true));
    expect(await listFolder("/Gone")).toBeNull();
  });

  it("reads names from ls output: summaries, shares, notes and damage left out", async () => {
    call.mockImplementationOnce(() =>
      answer(["Work/", 'garten/ (shared by "Alice", read)', "my shot.png  image 10×10 1 KB", "a b/", "(2 vaults in the database don't open)"].join("\n")),
    );
    expect(await listFolder("/")).toEqual(["Work/", "garten/", "my shot.png", "a b/"]);
  });
});
