// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import DeletionReceipt, { receiptText, replyText, type Receipt } from "./DeletionReceipt";

const receipt: Receipt = {
  userId: "u1",
  username: "anna",
  deletedBy: "chris",
  deletedAt: "2026-10-27T10:15:00Z",
  vaults: 2,
  notes: 41,
  apiKeys: 1,
  connections: 1,
  invitations: 1,
  externalProviders: ["OpenAI"],
  privacyContact: null,
  listed: true,
};

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("receiptText", () => {
  it("lists time, username, id, admin, counts, providers and the backup sentence, and no vault name", () => {
    const t = receiptText(receipt);
    expect(t).toContain("Deleted for a privacy request");
    expect(t).toContain("2026-10-27 10:15 UTC");
    expect(t).toContain("anna");
    expect(t).toContain("u1");
    expect(t).toContain("Deleted by: chris");
    expect(t).toContain("Vaults: 2");
    expect(t).toContain("Notes: 41");
    expect(t).toContain("API keys (incl. revoked): 1");
    expect(t).toContain("App connections (incl. revoked): 1");
    expect(t).toContain("Invitation they joined with: kept as used, its note and link to them erased (1)");
    expect(t).toContain("OpenAI: may have received text from vaults that used it; no copy was deleted there");
    expect(t).toContain("Backups are not reached by this deletion.");
    expect(t).not.toContain("Not recorded in the deletion list");
  });

  it("says 'none configured' without external providers", () => {
    expect(receiptText({ ...receipt, externalProviders: [] })).toContain("External embedding providers: none configured");
  });

  it("leaves out the invitation line when there was none", () => {
    expect(receiptText({ ...receipt, invitations: 0 })).not.toContain("Invitation they joined with");
  });

  it("says when the id isn't in the deletion list", () => {
    expect(receiptText({ ...receipt, listed: false })).toContain(
      "Not recorded in the deletion list (it isn't configured or couldn't be written).",
    );
  });
});

describe("replyText", () => {
  it("is the template with username, date and the contact placeholder", () => {
    expect(replyText(receipt)).toBe(`Subject: Your deletion request

Hello,

as you requested, we deleted your Kumbu² account "anna" and all notes in it on 2026-10-27 10:15 UTC. This includes every vault, its history, trash and checkpoints, your API keys and your app connections.

Copies that AI tools or other apps read from your notes are outside Kumbu². Please delete them there, for example in your chat history.

Our backups are not changed by a deletion; they are kept as described in our privacy notice. If a backup is ever restored, we delete your account again.

In future, if you still have access, you can export your notes first and delete your account yourself in Settings.

Kind regards
[your privacy contact]
`);
  });

  it("promises no deletion after a restore when the id isn't on the deletion list", () => {
    const reply = replyText({ ...receipt, listed: false });
    expect(reply).not.toContain("If a backup is ever restored");
    expect(reply).toContain("Our backups are not changed by a deletion; they are kept as described in our privacy notice.\n");
  });

  it("signs with the configured privacy contact", () => {
    expect(replyText({ ...receipt, privacyContact: "privacy@example.org" })).toMatch(/Kind regards\nprivacy@example\.org\n$/);
  });
});

describe("DeletionReceipt", () => {
  it("Copy reply writes replyText to the clipboard; Download receipt saves kumbu-deletion-<username>-2026-10-27.txt", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    let blob: Blob | undefined;
    let name: string | undefined;
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL: (b: Blob) => ((blob = b), "blob:kumbu/3"), revokeObjectURL: () => {} }));
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      name = this.download;
    });
    const onClose = vi.fn();
    render(<DeletionReceipt receipt={receipt} onClose={onClose} />);

    expect(screen.getByRole("heading", { name: "Deleted for a privacy request" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Copy reply" }));
    expect(writeText).toHaveBeenCalledWith(replyText(receipt));
    await userEvent.click(screen.getByRole("button", { name: "Download receipt" }));
    expect(name).toBe("kumbu-deletion-anna-2026-10-27.txt");
    expect(blob!.type).toMatch(/^text\/plain/);
    expect(await blob!.text()).toBe(receiptText(receipt));
    await userEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(onClose).toHaveBeenCalled();
  });

  it("warns next to Copy reply when the id isn't on the deletion list", () => {
    render(<DeletionReceipt receipt={{ ...receipt, listed: false }} onClose={() => {}} />);
    expect(
      screen.getByText(
        "This deletion isn't on the deletion list, so a restore that brings the account back won't be noticed. The reply leaves out the promise to delete it again; keep the user id and check for it after any restore.",
      ),
    ).toBeInTheDocument();
  });

  it("gives no such warning when the id is listed", () => {
    render(<DeletionReceipt receipt={receipt} onClose={() => {}} />);
    expect(screen.queryByText(/isn't on the deletion list/)).not.toBeInTheDocument();
  });

  it("says when copying didn't work", async () => {
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText: vi.fn().mockRejectedValue(new Error("denied")) } });
    render(<DeletionReceipt receipt={receipt} onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Copy reply" }));
    expect(await screen.findByText(/Copying didn't work/)).toBeInTheDocument();
  });
});
