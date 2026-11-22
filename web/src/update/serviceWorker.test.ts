// SPDX-License-Identifier: AGPL-3.0-or-later
import { refreshShell, registerShell } from "./serviceWorker";

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

function fakeWorkers() {
  const update = vi.fn(async () => undefined);
  const container = {
    register: vi.fn(async () => ({ update })),
    getRegistration: vi.fn(async () => ({ update })),
  };
  vi.stubGlobal("navigator", { ...navigator, serviceWorker: container });
  return { container, update };
}

test("a production build registers the app shell's worker for the whole app", async () => {
  vi.stubEnv("PROD", true);
  const { container } = fakeWorkers();

  await registerShell();

  expect(container.register).toHaveBeenCalledWith("/sw.js", { scope: "/", updateViaCache: "none" });
});

test("the dev server registers none: Vite serves the files itself", async () => {
  vi.stubEnv("PROD", false);
  const { container } = fakeWorkers();

  await registerShell();

  expect(container.register).not.toHaveBeenCalled();
});

test("a browser without service workers, or one that refuses, runs the app as before", async () => {
  vi.stubEnv("PROD", true);
  vi.stubGlobal("navigator", { ...navigator, serviceWorker: undefined });
  await expect(registerShell()).resolves.toBeUndefined();

  const { container } = fakeWorkers();
  container.register.mockRejectedValueOnce(new Error("SecurityError"));
  await expect(registerShell()).resolves.toBeUndefined();
});

test("refreshing asks the browser for the server's worker", async () => {
  const { update } = fakeWorkers();

  await refreshShell();

  expect(update).toHaveBeenCalledTimes(1);
});

test("refreshing without a worker, or offline, does nothing", async () => {
  const { container, update } = fakeWorkers();
  container.getRegistration.mockResolvedValueOnce(undefined as never);
  await expect(refreshShell()).resolves.toBeUndefined();

  update.mockRejectedValueOnce(new TypeError("Failed to fetch"));
  await expect(refreshShell()).resolves.toBeUndefined();
});
