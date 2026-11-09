// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json, noContent, user } from "../test/app";
import SkillsNotice from "./SkillsNotice";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function renderNotice(skillsNotice: { renamed: string; alias: boolean } | null) {
  let notice = skillsNotice;
  const dismissals: string[] = [];
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: true, publishEnabled: false, storageLimited: false, skillsNotice: notice }),
    "DELETE /api/account/skills-notice": (r) => (dismissals.push(new URL(r.url).pathname), (notice = null), noContent()),
  });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <SkillsNotice />
    </QueryClientProvider>,
  );
  return dismissals;
}

describe("SkillsNotice", () => {
  it("shows the rename notice and dismisses it", async () => {
    const dismissals = renderNotice({ renamed: "Skills (2)", alias: false });
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Your vault named Skills is now “Skills (2)”: the name Skills belongs to your new vault for skills. Its notes are untouched, and keys and apps that had it keep it under the new name.",
    );
    await userEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    await waitFor(() => expect(screen.queryByRole("status")).not.toBeInTheDocument());
    expect(dismissals).toEqual(["/api/account/skills-notice"]);
  });

  it("says which vault was renamed for an alias", async () => {
    renderNotice({ renamed: "Skills (shared)", alias: true });
    expect(await screen.findByRole("status")).toHaveTextContent(
      "The vault shared with you that you called Skills is now “Skills (shared)” for you: the name Skills belongs to your new vault for skills. Nothing in it changed.",
    );
  });

  it("shows nothing without a notice", async () => {
    renderNotice(null);
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });
});
