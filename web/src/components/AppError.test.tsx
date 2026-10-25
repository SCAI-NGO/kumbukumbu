// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { routerFuture } from "../router";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { page, RELOADED, RouteError } from "./AppError";

// What a redeploy does to an open tab: the old chunks are gone.
vi.mock("../routes/Keys", () => {
  throw new TypeError("Failed to fetch dynamically imported module: /assets/Keys-old.js");
});
vi.mock("../upload/UploadSheet", () => {
  throw new TypeError("Failed to fetch dynamically imported module: /assets/UploadSheet-old.js");
});

beforeEach(() => {
  // React and the router report a caught error on the console; here it is the point of the test.
  vi.spyOn(console, "error").mockImplementation(() => {});
  vi.spyOn(console, "warn").mockImplementation(() => {});
  vi.spyOn(page, "reload").mockImplementation(() => {});
  sessionStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
  sessionStorage.clear();
});

function renderFailing(error: Error) {
  const router = createMemoryRouter([{ path: "/", errorElement: <RouteError />, lazy: () => Promise.reject(error) }], { future: routerFuture });
  render(<RouterProvider router={router} future={{ v7_startTransition: true }} />);
}

const chunkError = () => new TypeError("Failed to fetch dynamically imported module: /assets/Keys-old.js");

describe("A part of the app that fails to load", () => {
  it("offers a reload instead of the router's raw error", async () => {
    renderFailing(new Error("Something broke"));

    expect(await screen.findByRole("heading", { name: "This page didn't load" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Reload" }));
    expect(page.reload).toHaveBeenCalledTimes(1);
  });

  it.each([
    "Failed to fetch dynamically imported module: /assets/Keys-old.js",
    "error loading dynamically imported module: /assets/Keys-old.js",
    "Importing a module script failed.",
  ])("reloads once by itself after an update (%s)", async (message) => {
    renderFailing(new TypeError(message));

    await waitFor(() => expect(page.reload).toHaveBeenCalledTimes(1));
    // A flag, nothing else: no content of the app.
    expect(sessionStorage.getItem(RELOADED)).toBe("1");
  });

  it("doesn't reload again when the reload didn't help", async () => {
    sessionStorage.setItem(RELOADED, "1");
    renderFailing(chunkError());

    expect(await screen.findByRole("heading", { name: "This page didn't load" })).toBeInTheDocument();
    expect(page.reload).not.toHaveBeenCalled();
  });

  it("doesn't reload by itself for any other error", async () => {
    renderFailing(new Error("Something broke"));

    await screen.findByRole("heading", { name: "This page didn't load" });
    expect(page.reload).not.toHaveBeenCalled();
    expect(sessionStorage.getItem(RELOADED)).toBeNull();
  });

  it("is caught for a page of the signed-in app", async () => {
    sessionStorage.setItem(RELOADED, "1");
    fakeApi({ "GET /api/session": () => json(200, { user, previousSignInAt: null }) });
    renderApp("/keys");

    expect(await screen.findByRole("heading", { name: "This page didn't load" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reload" })).toBeInTheDocument();
  });

  it("shows the same message in place of the upload sheet, and the shell stays", async () => {
    fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null }),
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 0 }]),
    });
    renderApp("/");
    await screen.findByRole("heading", { name: "Your vaults" });

    useEditor.getState().openUpload("/Work");

    expect(await screen.findByRole("heading", { name: "This didn't load" })).toBeInTheDocument();
    expect(screen.getByTestId("shell")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("heading", { name: "This didn't load" })).not.toBeInTheDocument();
    expect(useEditor.getState().overlay).toBe("none");
  });
});
