// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { bodiesOf } from "../test/calendar";
import { forgetZonePreset } from "./preferences";

const ZONES = ["America/New_York", "Asia/Tokyo", "Europe/London", "Europe/Vienna", "Pacific/Auckland"];

function api(zone: string | null, overrides: Parameters<typeof fakeApi>[0] = {}, days: string[] | null = null) {
  let stored = zone;
  let storedDays = days;
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3 }]),
    "GET /api/account/preferences": () => json(200, { askPasswordForDownloads: true }),
    "GET /api/vault-events": () => json(200, []),
    "GET /api/account/storage": () => json(200, { used: 4096, limit: null }),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/account/calendar": () => json(200, { zone: stored, serverZone: "UTC", days: storedDays }),
    "PUT /api/account/calendar": async (r) => {
      const body = (await r.clone().json()) as { zone?: string | null; days?: string[] | null };
      if (body.zone && !ZONES.includes(body.zone))
        return json(400, { error: `${body.zone} is not a time zone; choose one from the list (an IANA name such as Europe/Vienna).` });
      // A field left out stays as it is.
      if ("zone" in body) stored = body.zone ?? null;
      if ("days" in body) storedDays = body.days ?? null;
      return json(200, { zone: stored, serverZone: "UTC", days: storedDays });
    },
    ...overrides,
  });
}

function browserIn(zone: string) {
  const real = Intl.DateTimeFormat;
  vi.spyOn(Intl, "DateTimeFormat").mockImplementation(((...args: ConstructorParameters<typeof Intl.DateTimeFormat>) => {
    const format = new real(...args);
    const resolved = format.resolvedOptions();
    format.resolvedOptions = () => ({ ...resolved, timeZone: zone });
    return format;
  }) as unknown as typeof Intl.DateTimeFormat);
}

beforeEach(() => {
  forgetZonePreset();
  vi.spyOn(Intl, "supportedValuesOf").mockReturnValue(ZONES);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const reads = (fetch: ReturnType<typeof fakeApi>) =>
  fetch.mock.calls.filter(([r]) => (r as Request).method === "GET" && new URL((r as Request).url).pathname === "/api/account/calendar").length;

/** Long enough for a preset that follows the read to have gone out. */
const settle = () => new Promise((resolve) => setTimeout(resolve, 50));

/** The section once the zone is read. */
async function section() {
  const calendar = await screen.findByRole("region", { name: "Calendar" });
  await waitFor(() => expect(within(calendar).getByLabelText("Your time zone")).toBeEnabled());
  return calendar;
}

describe("the calendar's time zone", () => {
  it("presets the zone from the browser the first time", async () => {
    browserIn("Europe/Vienna");
    const fetch = api(null);
    renderApp("/");
    await waitFor(async () => expect(await bodiesOf(fetch, "PUT", "/api/account/calendar")).toEqual([{ zone: "Europe/Vienna" }]));
  });

  it("presets the zone on a deep link to the calendar too", async () => {
    browserIn("Europe/Vienna");
    const fetch = api(null, { "GET /api/vaults/Work/calendar/view": () => json(200, { view: "week", grid: false }) });
    renderApp("/calendar/Work");
    await waitFor(async () => expect(await bodiesOf(fetch, "PUT", "/api/account/calendar")).toEqual([{ zone: "Europe/Vienna" }]));
  });

  it("presets once per session", async () => {
    browserIn("Europe/Vienna");
    const fetch = api(null, { "PUT /api/account/calendar": () => json(500, { error: "Down." }) });
    renderApp("/");
    await waitFor(async () => expect(await bodiesOf(fetch, "PUT", "/api/account/calendar")).toHaveLength(1));
    cleanup();
    renderApp("/");
    await waitFor(() => expect(reads(fetch)).toBe(2));
    await settle();
    expect(await bodiesOf(fetch, "PUT", "/api/account/calendar")).toHaveLength(1);
    // A failed preset says nothing.
    expect(screen.queryByText("Down.")).not.toBeInTheDocument();
  });

  it("does not overwrite a stored zone", async () => {
    browserIn("Europe/Vienna");
    const fetch = api("Asia/Tokyo");
    renderApp("/");
    await waitFor(() => expect(reads(fetch)).toBe(1));
    await settle();
    expect(await bodiesOf(fetch, "PUT", "/api/account/calendar")).toEqual([]);
  });

  it("searches and saves a zone", async () => {
    const fetch = api("Europe/Vienna");
    renderApp("/settings");
    const calendar = await section();
    expect(await within(calendar).findByText(/Europe\/Vienna/)).toBeInTheDocument();
    const field = within(calendar).getByLabelText("Your time zone");
    await userEvent.clear(field);
    await userEvent.type(field, "lond");
    const matches = within(calendar).getByRole("list", { name: "Matching time zones" });
    expect(within(matches).queryByRole("button", { name: "Asia/Tokyo" })).not.toBeInTheDocument();
    await userEvent.click(within(matches).getByRole("button", { name: "Europe/London" }));
    expect(field).toHaveValue("Europe/London");
    await userEvent.click(within(calendar).getByRole("button", { name: "Save" }));
    await waitFor(async () => expect(await bodiesOf(fetch, "PUT", "/api/account/calendar")).toEqual([{ zone: "Europe/London" }]));
    expect(await within(calendar).findByRole("status")).toHaveTextContent("Saved.");
    expect(calendar).toHaveTextContent("Your calendar shows times in Europe/London.");
    expect(calendar).not.toHaveTextContent(/UTC|server/i);
  });

  it("searches with spaces for underscores", async () => {
    api("Europe/Vienna");
    renderApp("/settings");
    const calendar = await section();
    const field = within(calendar).getByLabelText("Your time zone");
    await userEvent.clear(field);
    await userEvent.type(field, "new york");
    expect(within(calendar).getByRole("button", { name: "America/New_York" })).toBeInTheDocument();
  });

  it("an unknown zone shows the server's sentence", async () => {
    api("Europe/Vienna");
    renderApp("/settings");
    const calendar = await section();
    const field = within(calendar).getByLabelText("Your time zone");
    await userEvent.clear(field);
    await userEvent.type(field, "Mars/Olympus");
    await userEvent.click(within(calendar).getByRole("button", { name: "Save" }));
    expect(await within(calendar).findByText(/Mars\/Olympus is not a time zone; choose one from the list/)).toBeInTheDocument();
    expect(calendar).toHaveTextContent("Your calendar shows times in Europe/Vienna.");
  });

  it("without a zone list, the field is a plain text field", async () => {
    vi.spyOn(Intl, "supportedValuesOf").mockImplementation(() => {
      throw new TypeError("not supported");
    });
    const fetch = api("Europe/Vienna");
    renderApp("/settings");
    const calendar = await section();
    const field = within(calendar).getByLabelText("Your time zone");
    await userEvent.clear(field);
    await userEvent.type(field, "Asia/Tokyo");
    expect(within(calendar).queryByRole("list", { name: "Matching time zones" })).not.toBeInTheDocument();
    await userEvent.click(within(calendar).getByRole("button", { name: "Save" }));
    await waitFor(async () => expect(await bodiesOf(fetch, "PUT", "/api/account/calendar")).toEqual([{ zone: "Asia/Tokyo" }]));
  });
});

const WEEK = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
const RESET = "Use my browser's weekend";

/** The level pressed on each day of the section's week, Monday first. */
const pressed = (calendar: HTMLElement) =>
  WEEK.map((day) => {
    const on = within(within(calendar).getByRole("group", { name: day })).getAllByRole("button", { pressed: true });
    return on.map((b) => b.textContent).join();
  });

const level = (calendar: HTMLElement, day: string, name: string) => within(within(calendar).getByRole("group", { name: day })).getByRole("button", { name });

/** The section once the week can be chosen. */
async function weekSection() {
  const calendar = await section();
  await waitFor(() => expect(level(calendar, "Monday", "Day off")).toBeEnabled());
  return calendar;
}

describe("the person's week", () => {
  it("shows the browser's weekend as days off until someone chooses", async () => {
    api("Europe/Vienna");
    renderApp("/settings");
    const calendar = await weekSection();
    expect(within(calendar).getByRole("group", { name: "Your week" })).toBeInTheDocument();
    expect(pressed(calendar)).toEqual(["Workday", "Workday", "Workday", "Workday", "Workday", "Day off", "Day off"]);
    expect(within(calendar).queryByRole("button", { name: RESET })).not.toBeInTheDocument();
  });

  it("a tap saves at once, all seven days as shown", async () => {
    const fetch = api("Europe/Vienna");
    renderApp("/settings");
    const calendar = await weekSection();
    await userEvent.click(level(calendar, "Monday", "Day off"));
    await waitFor(async () =>
      expect(await bodiesOf(fetch, "PUT", "/api/account/calendar")).toEqual([{ days: ["off", "workday", "workday", "workday", "workday", "off", "off"] }]),
    );
    await waitFor(() => expect(pressed(calendar)).toEqual(["Day off", "Workday", "Workday", "Workday", "Workday", "Day off", "Day off"]));
    // A second tap sends the week as it now is.
    await userEvent.click(level(calendar, "Friday", "Lighter day"));
    await waitFor(async () =>
      expect((await bodiesOf(fetch, "PUT", "/api/account/calendar")).at(-1)).toEqual({ days: ["off", "workday", "workday", "workday", "lighter", "off", "off"] }),
    );
  });

  it("Use my browser's weekend shows only while a choice is stored, and returns to not chosen", async () => {
    const fetch = api("Europe/Vienna");
    renderApp("/settings");
    const calendar = await weekSection();
    expect(within(calendar).queryByRole("button", { name: RESET })).not.toBeInTheDocument();
    await userEvent.click(level(calendar, "Sunday", "Workday"));
    const reset = await within(calendar).findByRole("button", { name: RESET });
    await waitFor(() => expect(reset).toBeEnabled());
    await userEvent.click(reset);
    await waitFor(async () => expect((await bodiesOf(fetch, "PUT", "/api/account/calendar")).at(-1)).toEqual({ days: null }));
    await waitFor(() => expect(within(calendar).queryByRole("button", { name: RESET })).not.toBeInTheDocument());
    expect(pressed(calendar)).toEqual(["Workday", "Workday", "Workday", "Workday", "Workday", "Day off", "Day off"]);
  });

  it("shows a stored week", async () => {
    api("Europe/Vienna", {}, ["off", "workday", "workday", "workday", "lighter", "workday", "workday"]);
    renderApp("/settings");
    const calendar = await weekSection();
    expect(pressed(calendar)).toEqual(["Day off", "Workday", "Workday", "Workday", "Lighter day", "Workday", "Workday"]);
    expect(within(calendar).getByRole("button", { name: RESET })).toBeInTheDocument();
  });

  it("saving the zone sends no days", async () => {
    const fetch = api("Europe/Vienna", {}, ["off", "workday", "workday", "workday", "workday", "off", "off"]);
    renderApp("/settings");
    const calendar = await section();
    const field = within(calendar).getByLabelText("Your time zone");
    await userEvent.clear(field);
    await userEvent.type(field, "Asia/Tokyo");
    await userEvent.click(within(calendar).getByRole("button", { name: "Save" }));
    await waitFor(async () => expect(await bodiesOf(fetch, "PUT", "/api/account/calendar")).toEqual([{ zone: "Asia/Tokyo" }]));
  });
});
