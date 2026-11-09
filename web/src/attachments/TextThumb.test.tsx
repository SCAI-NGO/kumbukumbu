// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { FileThumb } from "./Thumbnail";

afterEach(cleanup);

it("shows a text file as an icon, fetching and drawing nothing", () => {
  const fetch = vi.fn();
  vi.stubGlobal("fetch", fetch);
  const { container } = render(<FileThumb vault="Work" path="/Work/_skills/x/logo.svg" alt="logo" />);
  expect(fetch).not.toHaveBeenCalled();
  expect(container.querySelector("img, canvas")).toBeNull();
  expect(container.querySelector("svg[aria-label='logo']")).not.toBeNull();
  vi.unstubAllGlobals();
});
