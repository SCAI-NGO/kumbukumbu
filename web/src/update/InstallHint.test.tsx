// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { routerFuture } from "../router";
import InstallHint from "./InstallHint";

function device({ phone, standalone }: { phone: boolean; standalone: boolean }) {
  vi.stubGlobal("matchMedia", (q: string) => ({
    matches: (q === "(pointer: coarse)" && phone) || (q === "(display-mode: standalone)" && standalone),
    addEventListener() {},
    removeEventListener() {},
  }));
}

function renderHint() {
  const router = createMemoryRouter(
    [
      { path: "/", element: <InstallHint /> },
      { path: "/settings", element: <p>Settings page</p> },
    ],
    { initialEntries: ["/"], future: routerFuture },
  );
  render(<RouterProvider router={router} future={{ v7_startTransition: true }} />);
  return router;
}

const hint = () => screen.queryByRole("region", { name: "Install Kumbu² as an app" });

beforeEach(() => localStorage.clear());
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the install hint", () => {
  it("shows once on a phone, not when installed, not after Not now", async () => {
    device({ phone: true, standalone: false });
    renderHint();
    expect(hint()).toHaveTextContent("Install Kumbu² as an app");

    await userEvent.click(screen.getByRole("button", { name: "Not now" }));

    expect(hint()).not.toBeInTheDocument();
    expect({ ...localStorage }).toEqual({ "kumbu.installHint": "dismissed" });
    cleanup();
    renderHint();
    expect(hint()).not.toBeInTheDocument();
  });

  it("is not shown on a desktop pointer", () => {
    device({ phone: false, standalone: false });
    renderHint();
    expect(hint()).not.toBeInTheDocument();
  });

  it("is not shown in the installed app", () => {
    device({ phone: true, standalone: true });
    renderHint();
    expect(hint()).not.toBeInTheDocument();
  });

  it("is not shown where the browser can't tell", () => {
    renderHint();
    expect(hint()).not.toBeInTheDocument();
  });

  it("leads to the steps in Settings on How", async () => {
    device({ phone: true, standalone: false });
    const router = renderHint();

    await userEvent.click(screen.getByRole("button", { name: "How" }));

    expect(router.state.location.pathname).toBe("/settings");
    expect(router.state.location.hash).toBe("#install");
    expect(await screen.findByText("Settings page")).toBeInTheDocument();
  });
});
