// SPDX-License-Identifier: AGPL-3.0-or-later
import "fake-indexeddb/auto";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useOfflineCopy } from "../../offline/copy";
import { useOfflineMode } from "../../offline/mode";
import { turnOff } from "../../offline/turnOn";
import { useEditor } from "../../state/editor";
import { useSession } from "../../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../../test/app";

const key = Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString("base64url");
const work = { id: "v-work", name: "Work", notes: 3, canWrite: true, discreet: false, pending: false };
const diary = { id: "v-diary", name: "Diary", notes: 1, canWrite: true, discreet: true, pending: false };
const thisDevice = { id: "s1", label: "Firefox on Linux", createdAt: "2026-11-20T10:00:00Z", contactAt: null, waiting: 0, current: true };
const phone = { id: "s2", label: "Safari on iOS", createdAt: "2026-11-01T10:00:00Z", contactAt: "2026-11-19T10:00:00Z", waiting: 2, current: false };

/** The app's fake API, also for the store's requests, which fetch with a URL and options rather than a Request. */
function api(overrides: Record<string, (r: Request) => Response | Promise<Response>> = {}) {
  const fake = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [work, diary]),
    "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
    "GET /api/vault-events": () => json(200, []),
    "GET /api/account/storage": () => json(200, { used: 4096, limit: null }),
    "GET /api/account/calendar": () => json(200, { zone: "Europe/Vienna", serverZone: "Europe/Vienna" }),
    "GET /api/account/mirror-slots": () => json(200, [thisDevice, phone]),
    "POST /api/session/confirm": () => noContent(),
    "POST /api/account/mirror-slots": () => json(201, { id: "s1", token: "tok", key, vaults: [{ id: work.id, tag: "t", canWrite: true, pending: false }] }),
    "POST /api/account/mirror-slots/contact": () => json(200, { id: "s1", key, vaults: [{ id: work.id, tag: "t", canWrite: true, pending: false }] }),
    "DELETE /api/account/mirror-slots/*": () => noContent(),
    "PUT /api/account/mirror-slots/*": () => noContent(),
    ...overrides,
  });
  vi.stubGlobal("fetch", (input: Request | string, init?: RequestInit) =>
    fake(typeof input === "string" ? new Request(new URL(input, window.location.origin), init) : input));
  return fake;
}

const calls = (fake: ReturnType<typeof fakeApi>, key: string) =>
  fake.mock.calls.map(([r]) => r as Request).filter((r) => `${r.method} ${new URL(r.url).pathname}` === key);

// jsdom has no Web Locks: one tab, so a lock is only a queue.
let tail: Promise<unknown> = Promise.resolve();
const locks = {
  request: (_name: string, ...rest: unknown[]) => {
    const fn = rest[rest.length - 1] as () => Promise<unknown>;
    const run = tail.then(fn, fn);
    tail = run.catch(() => {});
    return run;
  },
};

beforeEach(() => {
  localStorage.clear();
  Object.defineProperty(navigator, "locks", { value: locks, configurable: true });
});

afterEach(async () => {
  const header = useOfflineCopy.getState().header;
  if (header) await turnOff(header);
  cleanup();
  useOfflineMode.setState({ offline: false });
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
});

async function turnedOn() {
  const fake = api();
  renderApp("/settings/offline");
  await userEvent.click(await screen.findByRole("switch", { name: "Keep my notes on this device" }));
  const dialog = screen.getByRole("alertdialog", { name: "Keep your notes on this device" });
  await userEvent.type(within(dialog).getByLabelText("Your password"), "correct horse");
  await userEvent.click(within(dialog).getByRole("button", { name: "Keep them here" }));
  await screen.findByRole("region", { name: "Vaults on this device" }, { timeout: 10_000 });
  return fake;
}

describe("Settings: Offline", () => {
  it("has a page of its own after Vaults, off by default, listing the devices with a copy", async () => {
    api({ "GET /api/account/mirror-slots": () => json(200, [phone]) });
    renderApp("/settings/offline");
    expect(await screen.findByRole("switch", { name: "Keep my notes on this device" })).not.toBeChecked();
    expect(screen.queryByRole("region", { name: "Vaults on this device" })).not.toBeInTheDocument();
    const devices = await screen.findByRole("list", { name: "Devices with a copy" });
    expect(devices).toHaveTextContent("Safari on iOS");
    expect(devices).toHaveTextContent(/last online .* · had 2 changes waiting/);
  });

  it("turns on with the password checked first, and lists every vault but the discreet ones", { timeout: 20_000 }, async () => {
    const fake = await turnedOn();
    expect(calls(fake, "POST /api/session/confirm")).toHaveLength(1);
    expect(await calls(fake, "POST /api/account/mirror-slots")[0]!.json()).toEqual({ label: expect.any(String) });
    expect(screen.getByRole("switch", { name: "Keep my notes on this device" })).toBeChecked();
    const vaults = screen.getByRole("list", { name: "Vaults on this device" });
    expect(within(vaults).getByRole("checkbox", { name: "Keep Work on this device" })).toBeChecked();
    expect(vaults).not.toHaveTextContent("Diary");
    expect(screen.getByRole("checkbox", { name: "All my vaults" })).toBeChecked();
    expect(screen.getByRole("combobox", { name: "Space for opened attachments" })).toHaveDisplayValue("500 MB");
    expect(within(screen.getByRole("list", { name: "Devices with a copy" })).getByText("This device")).toBeInTheDocument();
  });

  it("keeps nothing when the password is wrong", async () => {
    const fake = api({ "POST /api/session/confirm": () => json(400, { error: "The password is wrong." }) });
    renderApp("/settings/offline");
    await userEvent.click(await screen.findByRole("switch", { name: "Keep my notes on this device" }));
    const dialog = screen.getByRole("alertdialog");
    await userEvent.type(within(dialog).getByLabelText("Your password"), "wrong");
    await userEvent.click(within(dialog).getByRole("button", { name: "Keep them here" }));
    expect(await within(dialog).findByText("The password is wrong.")).toBeInTheDocument();
    expect(calls(fake, "POST /api/account/mirror-slots")).toHaveLength(0);
    expect(useOfflineCopy.getState().header).toBeNull();
  });

  it("chooses vaults one by one once All my vaults is off, and keeps the budget", { timeout: 20_000 }, async () => {
    await turnedOn();
    await userEvent.click(screen.getByRole("checkbox", { name: "Keep Work on this device" }));
    await waitFor(() => expect(useOfflineCopy.getState().mirror!.choice).toMatchObject({ all: false, vaults: [] }));
    expect(screen.getByRole("checkbox", { name: "All my vaults" })).not.toBeChecked();
    await userEvent.selectOptions(screen.getByRole("combobox", { name: "Space for opened attachments" }), "1 GB");
    await waitFor(() => expect(useOfflineCopy.getState().mirror!.choice.budget).toBe(1024 * 1_048_576));
  });

  it("lists under Kept offline only the vaults the session lists", { timeout: 20_000 }, async () => {
    await turnedOn();
    const mirror = useOfflineCopy.getState().mirror!;
    await act(() => mirror.choose((c) => ({ ...c, kept: { [work.id]: ["Projects"], "v-other": ["Plans"] } })));
    const kept = await screen.findByRole("list", { name: "Kept offline" });
    expect(kept).toHaveTextContent("Work / Projects");
    expect(kept).not.toHaveTextContent("Plans");
    expect(kept).not.toHaveTextContent("A vault");
    expect(within(kept).getAllByRole("listitem")).toHaveLength(1);
  });

  it("dims the vault choice, All my vaults and the budget offline, with Needs a connection", { timeout: 20_000 }, async () => {
    await turnedOn();
    const controls = () => [
      screen.getByRole("checkbox", { name: "All my vaults" }),
      screen.getByRole("checkbox", { name: "Keep Work on this device" }),
      screen.getByRole("combobox", { name: "Space for opened attachments" }),
    ];
    for (const c of controls()) expect(c).toBeEnabled();
    expect(screen.queryByText("Needs a connection")).toBeNull();
    act(() => useOfflineMode.setState({ offline: true }));
    for (const c of controls()) {
      expect(c).toBeDisabled();
      expect(c).toHaveAttribute("title", "Needs a connection");
    }
    expect(within(screen.getByRole("region", { name: "Vaults on this device" })).getByText("Needs a connection")).toBeInTheDocument();
    expect(within(screen.getByRole("region", { name: "Attachments" })).getByText("Needs a connection")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("checkbox", { name: "Keep Work on this device" }));
    expect(useOfflineCopy.getState().mirror!.choice).toMatchObject({ all: true });
    expect(screen.queryByText(/can't be reached/)).toBeNull();
  });

  it("renames a device's copy, this one too, and lists it under the new name", async () => {
    let label = phone.label;
    const fake = api({
      "GET /api/account/mirror-slots": () => json(200, [thisDevice, { ...phone, label }]),
      "PUT /api/account/mirror-slots/*": async (r) => {
        label = ((await r.json()) as { label: string }).label;
        return noContent();
      },
    });
    renderApp("/settings/offline");
    const devices = await screen.findByRole("list", { name: "Devices with a copy" });
    expect(within(devices).getAllByRole("button", { name: /^Rename / })).toHaveLength(2);
    await userEvent.click(within(devices).getByRole("button", { name: "Rename Safari on iOS" }));
    const dialog = screen.getByRole("dialog", { name: "Rename Safari on iOS" });
    const name = within(dialog).getByLabelText("New name");
    expect(within(dialog).getByRole("button", { name: "Rename" })).toBeDisabled();
    await userEvent.clear(name);
    await userEvent.type(name, "  My phone ");
    await userEvent.click(within(dialog).getByRole("button", { name: "Rename" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Rename Safari on iOS" })).not.toBeInTheDocument());
    const put = calls(fake, "PUT /api/account/mirror-slots/s2/label");
    expect(put).toHaveLength(1);
    expect(label).toBe("My phone");
    expect(await within(devices).findByText("My phone")).toBeInTheDocument();
  });

  it("keeps the rename dialog open with the server's sentence when it refuses", async () => {
    api({ "PUT /api/account/mirror-slots/*": () => json(400, { error: "A label is one line of 1 to 64 characters." }) });
    renderApp("/settings/offline");
    const devices = await screen.findByRole("list", { name: "Devices with a copy" });
    await userEvent.click(within(devices).getByRole("button", { name: "Rename Firefox on Linux" }));
    const dialog = screen.getByRole("dialog", { name: "Rename Firefox on Linux" });
    await userEvent.type(within(dialog).getByLabelText("New name"), " at work");
    await userEvent.click(within(dialog).getByRole("button", { name: "Rename" }));
    expect(await within(dialog).findByText("A label is one line of 1 to 64 characters.")).toBeInTheDocument();
  });

  it("removes another device's copy after naming what it hadn't uploaded", async () => {
    const fake = api();
    renderApp("/settings/offline");
    const devices = await screen.findByRole("list", { name: "Devices with a copy" });
    await userEvent.click(within(devices).getByRole("button", { name: "Remove offline copy" }));
    const dialog = screen.getByRole("alertdialog", { name: "Remove the copy on Safari on iOS?" });
    expect(dialog).toHaveTextContent("2 changes on that device haven't uploaded and will be lost.");
    // Changes waiting there: the password, as for a vault's deletion.
    expect(within(dialog).getByRole("button", { name: "Remove" })).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText("Your password"), "correct horse battery");
    await userEvent.click(within(dialog).getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(calls(fake, "POST /api/account/mirror-slots/s2/remove")).toHaveLength(1));
    expect(await calls(fake, "POST /api/account/mirror-slots/s2/remove")[0]!.json()).toEqual({ password: "correct horse battery" });
  });

  it("asks for the password when the device has just reported changes", async () => {
    let asked = 0;
    const fake = api({
      "GET /api/account/mirror-slots": () => json(200, [thisDevice, { ...phone, waiting: 0 }]),
      "DELETE /api/account/mirror-slots/*": () => {
        asked++;
        return json(400, { error: "This device has just reported changes waiting. Enter your password to remove its offline copy.", code: "changes-waiting" });
      },
      "POST /api/account/mirror-slots/*/remove": () => noContent(),
    });
    renderApp("/settings/offline");
    const devices = await screen.findByRole("list", { name: "Devices with a copy" });
    await userEvent.click(within(devices).getByRole("button", { name: "Remove offline copy" }));
    const dialog = screen.getByRole("alertdialog");
    expect(within(dialog).queryByLabelText("Your password")).toBeNull();
    await userEvent.click(within(dialog).getByRole("button", { name: "Remove" }));
    expect(await within(dialog).findByText(/just reported changes waiting/)).toBeInTheDocument();
    await userEvent.type(within(dialog).getByLabelText("Your password"), "correct horse battery");
    await userEvent.click(within(dialog).getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(calls(fake, "POST /api/account/mirror-slots/s2/remove")).toHaveLength(1));
    expect(asked).toBe(1);
  });

  it("asks for the password when the device's count isn't known yet, and says so", async () => {
    const fake = api({ "GET /api/account/mirror-slots": () => json(200, [thisDevice, { ...phone, waiting: null }]) });
    renderApp("/settings/offline");
    const devices = await screen.findByRole("list", { name: "Devices with a copy" });
    expect(devices).not.toHaveTextContent("waiting");
    await userEvent.click(within(devices).getByRole("button", { name: "Remove offline copy" }));
    const dialog = screen.getByRole("alertdialog");
    expect(dialog).toHaveTextContent("That device hasn't said yet whether it has changes waiting; any it has will be lost.");
    await userEvent.type(within(dialog).getByLabelText("Your password"), "correct horse battery");
    await userEvent.click(within(dialog).getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(calls(fake, "POST /api/account/mirror-slots/s2/remove")).toHaveLength(1));
  });

  it("shows the server's sentence when a removal is refused", async () => {
    api({
      "GET /api/account/mirror-slots": () => json(200, [thisDevice, { ...phone, waiting: 0 }]),
      "DELETE /api/account/mirror-slots/*": () => json(403, { error: "Only sessions remove offline copies." }),
    });
    renderApp("/settings/offline");
    const devices = await screen.findByRole("list", { name: "Devices with a copy" });
    await userEvent.click(within(devices).getByRole("button", { name: "Remove offline copy" }));
    const dialog = screen.getByRole("alertdialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Remove" }));
    expect(await within(dialog).findByText("Only sessions remove offline copies.")).toBeInTheDocument();
  });

  it("removes the copy from this device, and its place in the list", { timeout: 20_000 }, async () => {
    const fake = await turnedOn();
    await userEvent.click(screen.getByRole("button", { name: "Remove the copy" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(useOfflineCopy.getState().header).toBeNull());
    expect(calls(fake, "DELETE /api/account/mirror-slots/s1")).toHaveLength(1);
    expect(screen.getByRole("switch", { name: "Keep my notes on this device" })).not.toBeChecked();
  });
});
