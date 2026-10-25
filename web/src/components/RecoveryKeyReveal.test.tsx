// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { routerFuture } from "../router";
import { RecoveryKeyReveal } from "./RecoveryKeyReveal";

const key = "ABCD-EFGH-JKMN-PQRS";

function setup() {
  const onContinue = vi.fn();
  const router = createMemoryRouter(
    [
      { path: "/", element: <RecoveryKeyReveal value={key} username="ada" onContinue={onContinue} /> },
      { path: "/elsewhere", element: <p>Elsewhere</p> },
    ],
    { future: routerFuture },
  );
  render(<RouterProvider router={router} future={{ v7_startTransition: true }} />);
  return { router, onContinue };
}

function leaveWithReload() {
  const event = new Event("beforeunload", { cancelable: true });
  window.dispatchEvent(event);
  return event.defaultPrevented;
}

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("RecoveryKeyReveal", () => {
  it("lets the user continue only after copying and confirming", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    const { onContinue } = setup();
    const next = screen.getByRole("button", { name: "Continue" });

    expect(screen.getByText(key)).toBeInTheDocument();
    expect(next).toBeDisabled();
    await userEvent.click(screen.getByLabelText(/I have stored my recovery key/));
    expect(next).toBeDisabled();

    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    expect(writeText).toHaveBeenCalledWith(key);
    expect(next).toBeEnabled();
    await userEvent.click(next);
    expect(onContinue).toHaveBeenCalledOnce();
  });

  it("shows \"Copied\" for a moment after copying, announces it without the key, then goes back", async () => {
    vi.useFakeTimers();
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    setup();
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Copy" })));
    expect(screen.getByRole("button", { name: "Copied" })).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent(/^Copied$/);

    act(() => vi.advanceTimersByTime(2000));
    expect(screen.queryByRole("button", { name: "Copied" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Copy again" })).toBeInTheDocument();
    expect(screen.getByRole("status")).toBeEmptyDOMElement();
  });

  it("downloads the key from a blob URL that is revoked right after", () => {
    vi.useFakeTimers();
    const createObjectURL = vi.fn((_: Blob) => "blob:kumbu/1");
    const revokeObjectURL = vi.fn();
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL, revokeObjectURL }));
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    setup();

    fireEvent.click(screen.getByRole("button", { name: "Download" }));
    expect(click).toHaveBeenCalledOnce();
    expect(createObjectURL.mock.calls[0][0]).toBeInstanceOf(Blob);
    act(() => vi.runAllTimers());
    expect(revokeObjectURL).toHaveBeenCalledWith("blob:kumbu/1");
    expect(document.querySelector('a[href^="blob:"]')).toBeNull();

    fireEvent.click(screen.getByLabelText(/I have stored my recovery key/));
    expect(screen.getByRole("button", { name: "Continue" })).toBeEnabled();
  });

  it("puts the key into the downloaded file", async () => {
    let blob: Blob | undefined;
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL: (b: Blob) => ((blob = b), "blob:kumbu/2"), revokeObjectURL: () => {} }));
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    setup();
    await userEvent.click(screen.getByRole("button", { name: "Download" }));
    expect(await blob!.text()).toContain(key);
  });

  it("says so when copying fails, and doesn't count it as saved", async () => {
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: vi.fn().mockRejectedValue(new Error("denied")) } });
    setup();
    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    expect(await screen.findByText(/Copying didn't work/)).toBeInTheDocument();
    await userEvent.click(screen.getByLabelText(/I have stored my recovery key/));
    expect(screen.getByRole("button", { name: "Continue" })).toBeDisabled();
  });

  it("warns on reloading or closing the page until the key is saved", async () => {
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    setup();
    expect(leaveWithReload()).toBe(true);
    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    expect(leaveWithReload()).toBe(false);
  });

  it("asks once before leaving inside the app, and stays if asked to", async () => {
    const { router } = setup();

    await act(() => router.navigate("/elsewhere"));
    expect(screen.getByRole("alertdialog")).toHaveTextContent("You haven't saved your recovery key");
    expect(router.state.location.pathname).toBe("/");

    await userEvent.click(screen.getByRole("button", { name: "Stay" }));
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/");

    await act(() => router.navigate("/elsewhere"));
    await userEvent.click(screen.getByRole("button", { name: "Leave without it" }));
    expect(await screen.findByText("Elsewhere")).toBeInTheDocument();
  });

  it("moves focus to Stay, keeps it inside, and stays on Escape", async () => {
    const { router } = setup();
    await act(() => router.navigate("/elsewhere"));
    const stay = screen.getByRole("button", { name: "Stay" });
    expect(stay).toHaveFocus();
    await userEvent.tab({ shift: true });
    expect(screen.getByRole("button", { name: "Leave without it" })).toHaveFocus();
    await userEvent.tab();
    expect(stay).toHaveFocus();

    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/");
  });
});
