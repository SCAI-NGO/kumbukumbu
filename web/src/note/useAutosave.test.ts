// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SaveResult } from "../api/notes";
import { useAutosave } from "./useAutosave";

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

type Call = { content: string; version: string; answer(result: SaveResult | Error): void };

/** A save whose answers the test gives by hand, so it can type while one is in flight. */
function fakeSave() {
  const calls: Call[] = [];
  const save = vi.fn(
    (content: string, version: string) =>
      new Promise<SaveResult>((resolve, reject) => calls.push({ content, version, answer: (r) => (r instanceof Error ? reject(r) : resolve(r)) })),
  );
  return { calls, save };
}

const saved = (content: string, version: string): SaveResult => ({
  saved: { path: "/Work/plan.md", content, version, lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" } as never,
});

function setup() {
  const fake = fakeSave();
  const hook = renderHook(() => useAutosave({ content: "start", version: "v1" }, fake.save));
  return { ...fake, hook, current: () => hook.result.current };
}

/** Lets a settled save's handlers run. */
const settle = () => act(() => vi.advanceTimersByTimeAsync(0));

describe("autosave", () => {
  it("saves 800 ms after the last keystroke, with the known version", async () => {
    const { calls, current } = setup();
    act(() => current().change("start a"));
    await act(() => vi.advanceTimersByTimeAsync(500));
    act(() => current().change("start ab"));
    await act(() => vi.advanceTimersByTimeAsync(799));
    expect(calls).toHaveLength(0);
    await act(() => vi.advanceTimersByTimeAsync(1));
    expect(calls.map(({ content, version }) => ({ content, version }))).toEqual([{ content: "start ab", version: "v1" }]);
  });

  it("keeps one save in flight; a keystroke meanwhile saves the latest text after it, on the version it returned", async () => {
    const { calls, current } = setup();
    act(() => current().change("one"));
    await act(() => vi.advanceTimersByTimeAsync(800));
    act(() => current().change("one two"));
    await act(() => vi.advanceTimersByTimeAsync(800));
    act(() => current().change("one two three"));
    await act(() => vi.advanceTimersByTimeAsync(800));
    expect(calls).toHaveLength(1);

    calls[0].answer(saved("one", "v2"));
    await settle();
    expect(calls).toHaveLength(2);
    expect(calls[1]).toMatchObject({ content: "one two three", version: "v2" });
  });

  it("saves at once when asked (blur, page hidden), and not at all without a change", async () => {
    const { calls, current } = setup();
    act(() => current().saveNow());
    expect(calls).toHaveLength(0);
    act(() => current().change("start!"));
    act(() => current().saveNow());
    expect(calls).toHaveLength(1);
    calls[0].answer(saved("start!", "v2"));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(800));
    expect(calls).toHaveLength(1);
  });

  it("on a 409 keeps the user's text, pauses, and saves on top of the newer version only on keep mine", async () => {
    const { calls, current } = setup();
    act(() => current().change("mine"));
    act(() => current().saveNow());
    calls[0].answer({ conflict: { content: "theirs", version: "v7" } });
    await settle();
    expect(current().conflict).toEqual({ content: "theirs", version: "v7" });
    expect(current().value).toBe("mine");

    act(() => current().change("mine, more"));
    await act(() => vi.advanceTimersByTimeAsync(5000));
    act(() => current().saveNow());
    expect(calls).toHaveLength(1);

    act(() => void current().keepMine());
    expect(calls[1]).toMatchObject({ content: "mine, more", version: "v7" });
    expect(current().conflict).toBeNull();
  });

  it("asks again on a second 409 after keep mine, never on its own", async () => {
    const { calls, current } = setup();
    act(() => current().change("mine"));
    act(() => current().saveNow());
    calls[0].answer({ conflict: { content: "theirs", version: "v7" } });
    await settle();
    act(() => void current().keepMine());
    calls[1].answer({ conflict: { content: "theirs again", version: "v8" } });
    await settle();
    expect(current().conflict).toEqual({ content: "theirs again", version: "v8" });
    await act(() => vi.advanceTimersByTimeAsync(5000));
    expect(calls).toHaveLength(2);
  });

  it("on reload takes the newer text and version and saves on from there", async () => {
    const { calls, current } = setup();
    act(() => current().change("mine"));
    act(() => current().saveNow());
    calls[0].answer({ conflict: { content: "theirs", version: "v7" } });
    await settle();
    act(() => current().reload());
    expect(current().value).toBe("theirs");
    expect(current().conflict).toBeNull();
    act(() => current().change("theirs, and mine"));
    await act(() => vi.advanceTimersByTimeAsync(800));
    expect(calls[1]).toMatchObject({ content: "theirs, and mine", version: "v7" });
  });

  it("keeps the text when a save fails, says so, and retries on request", async () => {
    const { calls, current } = setup();
    act(() => current().change("mine"));
    act(() => current().saveNow());
    calls[0].answer(new TypeError("Failed to fetch"));
    await settle();
    expect(current().status).toBe("error");
    expect(current().value).toBe("mine");
    act(() => current().retry());
    expect(calls[1]).toMatchObject({ content: "mine", version: "v1" });
    calls[1].answer(saved("mine", "v2"));
    await settle();
    expect(current().status).toBe("saved");
  });

  it("flush waits for the saves and answers whether everything is saved", async () => {
    const { calls, current } = setup();
    act(() => current().change("one"));
    let done: boolean | undefined;
    act(() => void current().flush().then((d) => (done = d)));
    expect(calls).toHaveLength(1);
    calls[0].answer(saved("one", "v2"));
    await settle();
    expect(done).toBe(true);

    act(() => current().change("two"));
    done = undefined;
    act(() => void current().flush().then((d) => (done = d)));
    calls[1].answer({ conflict: { content: "theirs", version: "v7" } });
    await settle();
    expect(done).toBe(false);
  });
});
