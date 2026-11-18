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
      { call: "sh", command: "ls /Work" },
      { call: "sh", command: "ls" },
    ]);
  });

  it("answers parallel keystrokes with one request", async () => {
    await Promise.all([listFolder("/Work"), listFolder("/Work"), listFolder("/Work")]);
    expect(call).toHaveBeenCalledTimes(1);
  });

  it("quotes a folder with spaces for the shell", async () => {
    await listFolder("/My Vault/it's");
    expect(call.mock.calls[0][0]).toEqual({ call: "sh", command: `ls "/My Vault/it's"` });
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
