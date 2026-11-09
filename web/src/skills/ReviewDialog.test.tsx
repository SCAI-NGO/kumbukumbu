// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import { focusManager } from "@tanstack/react-query";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

const folder = "/Skills/pdf";
const copy = { name: "pdf", description: "Reads PDFs.", location: "Skills", path: folder, source: "github:acme/skills/pdf@abc", lastEditor: null, byOther: false, warnings: [], canWrite: true, update: "available" };
const review = {
  source: "github:acme/skills/pdf@def4567890123",
  repo: "acme/skills",
  path: "pdf",
  commit: "def4567890123456",
  hash: "HASH-1",
  files: [
    { path: "SKILL.md", change: "changed", hunks: ["@@ -1 +1 @@\n-old line\n+<b>new</b> line"] },
    { path: "extra.md", change: "added", hunks: ["@@ -0,0 +1 @@\n+extra"] },
    { path: "logo.png", change: "same", hunks: [] },
  ],
};

function api(extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, []),
    "GET /api/skills": () => json(200, { skills: [copy], withoutSkills: [] }),
    "POST /api/skills/updates/check": () => json(200, [{ path: folder, state: "available", source: copy.source, commit: "def4567890123456" }]),
    "GET /api/skills/updates/review": () => json(200, review),
    ...extra,
  });
}

async function open() {
  renderApp("/skills/installed");
  await userEvent.click(await screen.findByRole("button", { name: "Review" }));
  return screen.findByRole("dialog");
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
});

describe("ReviewDialog", () => {
  it("shows repository, path and commit first", async () => {
    api();
    const dialog = await open();
    await within(dialog).findByText("acme/skills");
    const text = dialog.textContent!;
    for (const t of ["Repository", "def456789012", "SKILL.md"]) expect(text).toContain(t);
    expect(text.indexOf("Repository")).toBeLessThan(text.indexOf("SKILL.md"));
    expect(text.indexOf("def456789012")).toBeLessThan(text.indexOf("SKILL.md"));
    expect(within(dialog).getByText("pdf")).toBeInTheDocument();
  });

  it("shows a shared link as such", async () => {
    api({ "GET /api/skills/updates/review": () => json(200, { ...review, source: "link:abc", repo: null, path: null, commit: null }) });
    const dialog = await open();
    expect(await within(dialog).findByText("Shared link")).toBeInTheDocument();
  });

  it("reviews every file and takes the update with the reviewed hash", async () => {
    let body: unknown;
    api({ "POST /api/skills/updates/take": async (r) => ((body = await r.json()), json(200, { path: folder, source: review.source, hash: "HASH-1" })) });
    const dialog = await open();
    // Hunks are plain text, never rendered.
    expect(await within(dialog).findByText(/<b>new<\/b> line/)).toBeInTheDocument();
    expect(dialog.querySelector("b")).toBeNull();
    for (const f of ["SKILL.md", "extra.md", "logo.png"]) expect(within(dialog).getByText(f)).toBeInTheDocument();
    expect(within(dialog).getByText("same")).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Take update" }));
    await waitFor(() => expect(body).toEqual({ path: folder, hash: "HASH-1" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("skips an update", async () => {
    let body: unknown;
    api({ "POST /api/skills/updates/skip": async (r) => ((body = await r.json()), noContent()) });
    const dialog = await open();
    await userEvent.click(await within(dialog).findByRole("button", { name: "Skip" }));
    await waitFor(() => expect(body).toEqual({ path: folder, hash: "HASH-1" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("says when the source moved on since the review", async () => {
    let reviews = 0;
    api({
      "GET /api/skills/updates/review": () => json(200, { ...review, hash: `HASH-${++reviews}` }),
      "POST /api/skills/updates/take": () => json(409, { error: "The source of /Skills/pdf changed since you reviewed it; review it again.", type: "moved-on" }),
    });
    const dialog = await open();
    await userEvent.click(await within(dialog).findByRole("button", { name: "Take update" }));
    expect(await within(dialog).findByText(/changed since you reviewed it/)).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Review again" }));
    await waitFor(() => expect(reviews).toBe(2));
    await waitFor(() => expect(within(dialog).queryByText(/changed since you reviewed it/)).not.toBeInTheDocument());
  });

  it("shows a refusal such as the rate limit", async () => {
    api({ "GET /api/skills/updates/review": () => json(429, { error: "GitHub is asked too often; try again in a minute." }) });
    const dialog = await open();
    expect(await within(dialog).findByText("GitHub is asked too often; try again in a minute.")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Take update" })).toBeDisabled();
  });

  it("offers neither Take nor Skip while a re-review loads or after it failed", async () => {
    let reviews = 0;
    let release: () => void = () => {};
    api({
      "GET /api/skills/updates/review": async () => {
        if (++reviews === 1) return json(200, review);
        if (reviews === 2) await new Promise<void>((r) => (release = r));
        return reviews === 2 ? json(200, review) : json(502, { error: "GitHub failed." });
      },
      "POST /api/skills/updates/take": () => json(409, { error: "The source changed since you reviewed it; review it again.", type: "moved-on" }),
    });
    const dialog = await open();
    await userEvent.click(await within(dialog).findByRole("button", { name: "Take update" }));
    await userEvent.click(await within(dialog).findByRole("button", { name: "Review again" }));
    await waitFor(() => expect(reviews).toBe(2));
    expect(within(dialog).getByRole("button", { name: "Take update" })).toBeDisabled();
    expect(within(dialog).getByRole("button", { name: "Skip" })).toBeDisabled();
    release();
    await waitFor(() => expect(within(dialog).getByRole("button", { name: "Take update" })).toBeEnabled());
  });

  it("keeps the reviewed hash when the window regains focus", async () => {
    let reviews = 0;
    api({ "GET /api/skills/updates/review": () => json(200, { ...review, hash: `HASH-${++reviews}` }) });
    const dialog = await open();
    await within(dialog).findByText("acme/skills");
    focusManager.setFocused(false);
    focusManager.setFocused(true);
    await new Promise((r) => setTimeout(r, 50));
    expect(reviews).toBe(1);
  });
});
