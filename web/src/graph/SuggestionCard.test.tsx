// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render as rtlRender, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useDialog } from "../hooks/useDialog";
import { fakeApi, json } from "../test/app";
import SuggestionCard from "./SuggestionCard";

const FROM = "/Work/Pump.md";
let bodies: unknown[] = [];
let level = "owner";

beforeEach(() => {
  bodies = [];
  level = "owner";
  const answered = async (r: Request) => {
    bodies.push(await r.json());
    return json(200, { path: FROM, version: "v2" });
  };
  fakeApi({
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2, level }]),
    "POST /api/vaults/Work/similar/link": answered,
    "POST /api/vaults/Work/similar/not-related": answered,
    "GET /api/*": () => json(200, {}),
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const client = () => new QueryClient({ defaultOptions: { queries: { retry: false } } });
const render = (ui: React.ReactElement) => rtlRender(<QueryClientProvider client={client()}>{ui}</QueryClientProvider>);

const suggestion = { path: "/Work/Plan.md", title: "Plan", similarity: { score: 0.7, measured: true } };

function show() {
  const onClose = vi.fn();
  render(
    <div>
      <button type="button">elsewhere</button>
      <SuggestionCard from={FROM} suggestion={suggestion} onClose={onClose} />
    </div>,
  );
  return onClose;
}

describe("SuggestionCard", () => {
  it("names the note; Add link writes the link and closes the card", async () => {
    const onClose = show();
    expect(screen.getByRole("dialog", { name: "Plan" })).toHaveTextContent("Similar in meaning, not linked");
    await userEvent.click(screen.getByRole("button", { name: "Add link" }));
    await vi.waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(bodies).toEqual([{ path: FROM, target: "/Work/Plan.md" }]);
  });

  it("Not related hides it, and Undo takes that back", async () => {
    const onClose = show();
    await userEvent.click(screen.getByRole("button", { name: "Not related" }));
    expect(await screen.findByText("Hidden: not suggested again")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Undo" }));
    await vi.waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(bodies).toEqual([
      { path: FROM, other: "/Work/Plan.md", undo: false },
      { path: FROM, other: "/Work/Plan.md", undo: true },
    ]);
  });

  it("offers no answers where the note can't be changed", async () => {
    level = "read";
    show();
    await vi.waitFor(() => expect(screen.queryByRole("button", { name: "Add link" })).not.toBeInTheDocument());
    expect(screen.queryByRole("button", { name: "Not related" })).not.toBeInTheDocument();
  });

  it("closes on Escape", async () => {
    const onClose = show();
    await userEvent.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("keeps its Escape from the dialog it is in, so that one closes only the card", async () => {
    const onClose = vi.fn();
    const closeDialog = vi.fn();
    function Holder() {
      const ref = useDialog<HTMLDivElement>(closeDialog);
      return (
        <div ref={ref} role="dialog" aria-label="Graph">
          <SuggestionCard from={FROM} suggestion={suggestion} onClose={onClose} />
        </div>
      );
    }
    render(<Holder />);
    await userEvent.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(closeDialog).not.toHaveBeenCalled();
  });

  it("with no dialog around it, closes on Escape and lets the Escape go on (to the editor's own)", async () => {
    const reached = vi.fn();
    document.addEventListener("keydown", reached);
    try {
      const onClose = show();
      await userEvent.keyboard("{Escape}");
      expect(onClose).toHaveBeenCalledTimes(1);
      expect(reached).toHaveBeenCalledTimes(1);
      expect(reached.mock.calls[0][0].defaultPrevented).toBe(false);
    } finally {
      document.removeEventListener("keydown", reached);
    }
  });

  it("leaves Escape to a dialog opened above it", async () => {
    const onClose = show();
    const closeDialog = vi.fn();
    function Above() {
      const ref = useDialog<HTMLDivElement>(closeDialog);
      return <div ref={ref} role="dialog" aria-label="Above" />;
    }
    render(<Above />);
    await userEvent.keyboard("{Escape}");
    expect(closeDialog).toHaveBeenCalledTimes(1);
    expect(onClose).not.toHaveBeenCalled();
  });

  it("closes on a click outside, not on one inside", async () => {
    const onClose = show();
    await userEvent.click(screen.getByText("Similar in meaning, not linked"));
    expect(onClose).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "elsewhere" }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
