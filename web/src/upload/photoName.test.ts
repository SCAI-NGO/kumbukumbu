// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { extOf, photoName } from "./photoName";

describe("photoName", () => {
  it("names by local time", () => {
    expect(photoName(new Date(2026, 10, 14, 9, 5, 7), "jpg", new Set())).toBe("Photo 2026-11-14 090507.jpg");
  });

  it("a name taken gets 2, then 3, ignoring case", () => {
    const at = new Date(2026, 10, 14, 21, 30, 0);
    expect(photoName(at, "jpg", new Set(["Photo 2026-11-14 213000.jpg"]))).toBe("Photo 2026-11-14 213000 2.jpg");
    expect(photoName(at, "jpg", new Set(["photo 2026-11-14 213000.JPG", "Photo 2026-11-14 213000 2.jpg"]))).toBe("Photo 2026-11-14 213000 3.jpg");
  });
});

describe("extOf", () => {
  it.each([
    ["image/jpeg", "jpg"],
    ["image/png", "png"],
    ["image/gif", "gif"],
    ["image/webp", "webp"],
  ])("%s is stored as .%s", (type, ext) => expect(extOf(type)).toBe(ext));

  it.each(["image/heic", "image/heif", "", "application/octet-stream"])("%s has no photo name of its own", (type) => expect(extOf(type)).toBeNull());
});
