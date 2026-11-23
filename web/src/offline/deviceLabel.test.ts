// SPDX-License-Identifier: AGPL-3.0-or-later
import { expect, test } from "vitest";
import { deviceLabel } from "./deviceLabel";

test("names browser and system, the more specific browser first", () => {
  expect(deviceLabel("Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0")).toBe("Firefox on Linux");
  expect(deviceLabel("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36 Edg/140.0")).toBe("Edge on Windows");
  expect(deviceLabel("Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Mobile Safari/537.36")).toBe("Chrome on Android");
  expect(deviceLabel("Mozilla/5.0 (iPhone; CPU iPhone OS 19_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/19.0 Mobile/15E148 Safari/604.1")).toBe("Safari on iOS");
  expect(deviceLabel("Mozilla/5.0 (Macintosh; Intel Mac OS X 15_0) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/19.0 Safari/605.1.15")).toBe("Safari on macOS");
  expect(deviceLabel("curl/8")).toBe("A browser");
});
