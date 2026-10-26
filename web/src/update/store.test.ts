// SPDX-License-Identifier: AGPL-3.0-or-later
import { api, call } from "../api/client";
import { fakeApi, json } from "../test/app";
import { checkVersion, useUpdate } from "./store";

vi.mock("./version", async (importOriginal) => ({ ...(await importOriginal<typeof import("./version")>()), appVersion: "0.1.0-pre.41+aaaaaaa" }));

const initial = useUpdate.getState();

afterEach(() => {
  useUpdate.setState(initial, true);
  vi.unstubAllGlobals();
});

const withVersion = (version: string | null) => {
  const response = json(200, []);
  if (version) response.headers.set("X-Kumbu-Version", version);
  return response;
};

test("every API response reports the header", async () => {
  fakeApi({ "GET /api/vaults": () => withVersion("0.2.0+a6362ec") });

  await call(api.GET("/api/vaults"));

  expect(useUpdate.getState().server).toBe("0.2.0+a6362ec");
  expect(useUpdate.getState().available?.semver).toBe("0.2.0");
});

test("a response without the header changes nothing", async () => {
  useUpdate.getState().report("0.2.0+a6362ec");
  fakeApi({ "GET /api/vaults": () => withVersion(null) });

  await call(api.GET("/api/vaults"));

  expect(useUpdate.getState().server).toBe("0.2.0+a6362ec");
  expect(useUpdate.getState().available).toEqual({ semver: "0.2.0", sha: "a6362ec" });
});

test("the same build on the server is no update", async () => {
  fakeApi({ "GET /api/version": () => withVersion("0.1.0-pre.41+aaaaaaa") });

  await checkVersion();

  expect(useUpdate.getState().server).toBe("0.1.0-pre.41+aaaaaaa");
  expect(useUpdate.getState().available).toBeNull();
});

test("checkVersion swallows a network error", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));

  await expect(checkVersion()).resolves.toBeUndefined();
  expect(useUpdate.getState().available).toBeNull();
});
