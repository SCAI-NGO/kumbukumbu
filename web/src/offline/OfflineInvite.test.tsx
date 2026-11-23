// SPDX-License-Identifier: AGPL-3.0-or-later
import "fake-indexeddb/auto";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, test } from "vitest";
import { useSession } from "../state/session";
import { user } from "../test/app";
import OfflineInvite from "./OfflineInvite";
import { rememberSignIn } from "./turnOn";

const show = () =>
  render(
    <MemoryRouter>
      <Routes>
        <Route path="/" element={<OfflineInvite />} />
        <Route path="/settings/offline" element={<p>Offline settings</p>} />
      </Routes>
    </MemoryRouter>,
  );

beforeEach(() => {
  localStorage.clear();
  useSession.getState().signedIn({ user, previousSignInAt: null });
});

afterEach(() => {
  cleanup();
  useSession.getState().reset();
});

describe("the invitation to keep the notes offline", () => {
  test("comes once, after a sign-in with Keep me signed in, and leads to Settings, Offline", async () => {
    rememberSignIn(true);
    show();
    await userEvent.click(await screen.findByRole("button", { name: "Set up" }));
    expect(screen.getByText("Offline settings")).toBeInTheDocument();
    cleanup();
    show();
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByRole("region", { name: "Keep your notes on this device" })).not.toBeInTheDocument();
  });

  test("never comes on a device that may not be the person's own", async () => {
    rememberSignIn(false);
    show();
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByRole("region", { name: "Keep your notes on this device" })).not.toBeInTheDocument();
  });
});
