// SPDX-License-Identifier: AGPL-3.0-or-later
// The name a device's offline copy goes by in the device list (design §3a, "Devices with a copy"): browser and system.

const browsers: [RegExp, string][] = [[/Edg\//, "Edge"], [/OPR\//, "Opera"], [/Firefox\//, "Firefox"], [/Chrome\//, "Chrome"], [/Safari\//, "Safari"]];
const systems: [RegExp, string][] = [[/Android/, "Android"], [/iPhone|iPad/, "iOS"], [/Mac OS X|Macintosh/, "macOS"], [/Windows/, "Windows"], [/CrOS/, "ChromeOS"], [/Linux/, "Linux"]];

/** "Firefox on Linux"; "A browser" for what isn't recognised. */
export function deviceLabel(userAgent = navigator.userAgent): string {
  const browser = browsers.find(([re]) => re.test(userAgent))?.[1] ?? "A browser";
  const system = systems.find(([re]) => re.test(userAgent))?.[1];
  return system === undefined ? browser : `${browser} on ${system}`;
}
