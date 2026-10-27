// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from "vitest";
import { browserZone, checkpointExportUrl, download, exportUrl } from "./exportUrl";

afterEach(() => vi.restoreAllMocks());

describe("exportUrl", () => {
  it("is the vault's own route for one vault", () => {
    expect(exportUrl(["Work"], "Europe/Vienna")).toBe("/api/vaults/Work/export?tz=Europe%2FVienna");
    expect(exportUrl(["A b/c"], "UTC")).toBe("/api/vaults/A%20b%2Fc/export?tz=UTC");
  });

  it("names each vault once in the query for several", () => {
    expect(exportUrl(["A b", "Ü"], "Europe/Vienna")).toBe("/api/export?vault=A%20b&vault=%C3%9C&tz=Europe%2FVienna");
  });

  it("sends the browser's time zone by default", () => {
    vi.spyOn(Intl.DateTimeFormat.prototype, "resolvedOptions").mockReturnValue({ timeZone: "America/New_York" } as Intl.ResolvedDateTimeFormatOptions);
    expect(browserZone()).toBe("America/New_York");
    expect(exportUrl(["Work"])).toBe("/api/vaults/Work/export?tz=America%2FNew_York");
  });
});

describe("checkpointExportUrl", () => {
  it("is the checkpoint's own route with the reader's time zone", () => {
    expect(checkpointExportUrl("My Diary", "before-cleanup", "Europe/Vienna")).toBe("/api/vaults/My%20Diary/checkpoints/before-cleanup/export?tz=Europe%2FVienna");
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
