// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { crumbOf, fileKindOf, fileUrl, folderOf, isAttachment, isMarker, markerOf, noteUrl, pathFromRoute, shownPath, titleOf, untitledName, viewUrl } from "./paths";

describe("note paths", () => {
  it("puts a note into the URL one encoded segment at a time", () => {
    expect(noteUrl("/My Vault/a b/c%?#.md")).toBe("/notes/My%20Vault/a%20b/c%25%3F%23.md");
  });

  it("reads the path back from the route's decoded params", () => {
    expect(pathFromRoute("My Vault", "a b/c%?#.md")).toBe("/My Vault/a b/c%?#.md");
  });

  it("names a note by its file name without .md, and its place by vault and folders", () => {
    expect(titleOf("/Work/projects/plan.md")).toBe("plan");
    expect(crumbOf("/Work/projects/deep/plan.md")).toBe("Work / projects / deep");
    expect(folderOf("/Work/projects/plan.md")).toBe("/Work/projects");
    expect(folderOf("/Work/plan.md")).toBe("/Work");
  });

  it("picks the first free Untitled name in the folder", () => {
    expect(untitledName([])).toBe("Untitled.md");
    expect(untitledName(["plan.md"])).toBe("Untitled.md");
    expect(untitledName(["Untitled.md", "untitled 2.md", "Untitled 4.md"])).toBe("Untitled 3.md");
  });
});

describe("attachment paths", () => {
  it("tells attachments from notes by the extensions the server keeps, in any case", () => {
    expect(fileKindOf("/Work/shot.PNG")).toBe("image");
    expect(fileKindOf("/Work/a.jpeg")).toBe("image");
    expect(fileKindOf("/Work/report.pdf")).toBe("pdf");
    expect(fileKindOf("/Work/plan.md")).toBe("note");
    expect(fileKindOf("/Work/x.png/plan.md")).toBe("note");
    expect(isAttachment("/Work/memo.docx")).toBe(false);
    expect(isAttachment("/Work/x.webp")).toBe(true);
  });

  it("keeps an attachment's extension in its title", () => {
    expect(titleOf("/Work/Screenshots/shot.png")).toBe("shot.png");
  });

  it("puts an attachment into the API's and the lightbox's URL one encoded segment at a time", () => {
    expect(fileUrl("My Vault", "a b/c#?.png")).toBe("/api/files/My%20Vault/a%20b/c%23%3F.png");
    expect(fileUrl("W", "x.pdf", "abc123")).toBe("/api/files/W/x.pdf?version=abc123");
    expect(viewUrl("/My Vault/a b/c#.png")).toBe("/view/My%20Vault/a%20b/c%23.png");
  });
});

describe("folder markers", () => {
  it("fileKindOf names a marker a folder and nothing else", () => {
    expect(fileKindOf("/Work/Drafts/.vaultkeep")).toBe("folder");
    expect(isMarker("/Work/Drafts/.vaultkeep")).toBe(true);
    expect(isAttachment("/Work/Drafts/.vaultkeep")).toBe(false);
    expect(fileKindOf("/Work/Drafts/x.vaultkeep")).toBe("note");
    expect(fileKindOf("/Work/Drafts/.VaultKeep")).toBe("note");
    expect(isMarker("/Work/.vaultkeep/plan.md")).toBe(false);
  });

  it("markerOf and shownPath", () => {
    expect(markerOf("/Work/Drafts")).toBe("Drafts/.vaultkeep");
    expect(markerOf("/Work/Drafts/2026")).toBe("Drafts/2026/.vaultkeep");
    expect(shownPath("/Work/Drafts/.vaultkeep")).toBe("/Work/Drafts/");
    expect(shownPath("/Work/plan.md")).toBe("/Work/plan.md");
  });
});
