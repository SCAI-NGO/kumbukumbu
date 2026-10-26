// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from "vitest";
import { download, exportUrl } from "./exportUrl";

afterEach(() => vi.restoreAllMocks());

describe("exportUrl", () => {
  it("is the vault's own route for one vault", () => {
    expect(exportUrl(["Work"])).toBe("/api/vaults/Work/export");
    expect(exportUrl(["A b/c"])).toBe("/api/vaults/A%20b%2Fc/export");
  });

  it("names each vault once in the query for several", () => {
    expect(exportUrl(["A b", "Ü"])).toBe("/api/export?vault=A%20b&vault=%C3%9C");
  });
});

describe("download", () => {
  it("clicks a temporary link with the download attribute, then removes it", () => {
    const clicked: HTMLAnchorElement[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      clicked.push(this);
    });

    download("/api/vaults/Work/export");

    expect(clicked).toHaveLength(1);
    expect(clicked[0].getAttribute("href")).toBe("/api/vaults/Work/export");
    expect(clicked[0].hasAttribute("download")).toBe(true);
    expect(document.querySelector("a[download]")).toBeNull();
  });
});
