// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import PinDialog from "./PinDialog";
import { useDiscreet } from "./state";

const storageKey = "kumbu.discreetTimeout";
const wrongPin = "You have no discreet vaults.";
const tooMany = "Too many attempts; try again in a minute.";

beforeEach(() => {
  try {
    localStorage.clear();
    sessionStorage.clear();
  } catch {
    // Some runs have no storage; the tests that need it set it up.
  }
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
});

const revealed = (until: string | null) => json(200, { revealed: true, until });

function renderDialog(onClose = vi.fn()) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <PinDialog onClose={onClose} />
    </QueryClientProvider>,
  );
  return { onClose, queryClient, dialog: screen.getByRole("dialog", { name: "More vaults" }) };
}

const chosen = (dialog: HTMLElement) => within(dialog).getAllByRole("radio").find((r) => (r as HTMLInputElement).checked)?.closest("label")?.textContent;
const sentBodies = (fetch: ReturnType<typeof fakeApi>) =>
  Promise.all(
    fetch.mock.calls
      .map(([r]) => r as Request)
      .filter((r) => r.method === "POST" && new URL(r.url).pathname === "/api/account/discreet/reveal")
      .map((r) => r.clone().json() as Promise<unknown>),
  );

async function enter(dialog: HTMLElement, pin: string) {
  await userEvent.type(within(dialog).getByLabelText("PIN"), pin);
  await userEvent.click(within(dialog).getByRole("button", { name: "Open" }));
}

describe("PinDialog", () => {
  it("asks for the PIN and how long, in a field no manager fills", () => {
    const { dialog } = renderDialog();
    expect(within(dialog).getByRole("heading")).toHaveTextContent("More vaults");
    const field = within(dialog).getByLabelText("PIN");
    expect(field).toHaveAttribute("type", "password");
    expect(field).toHaveAttribute("inputmode", "numeric");
    expect(field).toHaveAttribute("autocomplete", "off");
    expect(field).toHaveAttribute("maxlength", "6");
    expect(field).toHaveFocus();
    const group = within(dialog).getByRole("group", { name: "Hide again after" });
    expect(within(group).getAllByRole("radio").map((r) => r.closest("label")?.textContent)).toEqual(["5 min", "10 min", "30 min", "60 min", "Never"]);
    expect(within(dialog).getByRole("button", { name: "Open" })).toBeDisabled();
  });

  it("preselects this device's last choice", () => {
    localStorage.setItem(storageKey, "30");
    expect(chosen(renderDialog().dialog)).toBe("30 min");
  });

  it("preselects 10 minutes with nothing stored, or something else stored", () => {
    expect(chosen(renderDialog().dialog)).toBe("10 min");
    cleanup();
    localStorage.setItem(storageKey, "45");
    expect(chosen(renderDialog().dialog)).toBe("10 min");
  });

  it("preselects 10 minutes where storage throws, and still opens", async () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new DOMException("blocked", "SecurityError");
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new DOMException("blocked", "SecurityError");
    });
    const fetch = fakeApi({ "POST /api/account/discreet/reveal": () => revealed(null) });
    const { dialog, onClose } = renderDialog();
    expect(chosen(dialog)).toBe("10 min");
    await enter(dialog, "1234");
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(await sentBodies(fetch)).toEqual([{ pin: "1234", minutes: 10 }]);
  });

  it("Open sends the PIN and the minutes, stores the choice and nothing else, and closes", async () => {
    const local = vi.spyOn(Storage.prototype, "setItem");
    const fetch = fakeApi({ "POST /api/account/discreet/reveal": () => revealed("2026-11-09T10:30:00Z") });
    const { dialog, onClose } = renderDialog();
    await userEvent.click(within(dialog).getByRole("radio", { name: "30 min" }));
    await enter(dialog, "482916");
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(await sentBodies(fetch)).toEqual([{ pin: "482916", minutes: 30 }]);
    expect(localStorage.getItem(storageKey)).toBe("30");
    // The one write, of the choice; the PIN goes nowhere but the request.
    expect(local.mock.calls).toEqual([[storageKey, "30"]]);
    expect(local.mock.contexts).toEqual([localStorage]);
    expect(sessionStorage.length).toBe(0);
    expect(useDiscreet.getState()).toMatchObject({ revealed: true, until: "2026-11-09T10:30:00Z" });
  });

  it("Never sends no minutes", async () => {
    const fetch = fakeApi({ "POST /api/account/discreet/reveal": () => revealed(null) });
    const { dialog, onClose } = renderDialog();
    await userEvent.click(within(dialog).getByRole("radio", { name: "Never" }));
    await enter(dialog, "1234");
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(await sentBodies(fetch)).toEqual([{ pin: "1234", minutes: null }]);
    expect(localStorage.getItem(storageKey)).toBe("never");
  });

  it("takes digits only, up to six", async () => {
    const { dialog } = renderDialog();
    const field = within(dialog).getByLabelText("PIN");
    await userEvent.type(field, "12a3");
    expect(field).toHaveValue("123");
    expect(within(dialog).getByRole("button", { name: "Open" })).toBeDisabled();
    await userEvent.type(field, "4");
    expect(within(dialog).getByRole("button", { name: "Open" })).toBeEnabled();
    await userEvent.type(field, "5678");
    expect(field).toHaveValue("123456");
  });

  it("a wrong PIN shows the server's sentence and clears the field", async () => {
    const local = vi.spyOn(Storage.prototype, "setItem");
    fakeApi({ "POST /api/account/discreet/reveal": () => json(400, { error: wrongPin }) });
    const { dialog, onClose } = renderDialog();
    await enter(dialog, "0000");
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(wrongPin);
    expect(within(dialog).getByLabelText("PIN")).toHaveValue("");
    expect(onClose).not.toHaveBeenCalled();
    expect(useDiscreet.getState().revealed).toBe(false);
    // Typing again takes the sentence away.
    await userEvent.type(within(dialog).getByLabelText("PIN"), "1");
    expect(within(dialog).queryByRole("alert")).not.toBeInTheDocument();
    expect(local.mock.calls.every(([key]) => key === storageKey)).toBe(true);
    expect(sessionStorage.length).toBe(0);
  });

  it("over the limit, shows the server's sentence", async () => {
    fakeApi({ "POST /api/account/discreet/reveal": () => json(429, { error: tooMany }) });
    const { dialog, onClose } = renderDialog();
    await enter(dialog, "1234");
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(tooMany);
    expect(onClose).not.toHaveBeenCalled();
  });

  it("Cancel and Escape close it", async () => {
    const { dialog, onClose } = renderDialog();
    await userEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    expect(onClose).toHaveBeenCalledTimes(1);
    await userEvent.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalledTimes(2);
  });
});

describe("More vaults… in the shell", () => {
  const vault = (name: string, discreet = false) => ({ name, notes: 1, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false, discreet });

  it("opens the dialog from the vault menu; the right PIN shows the discreet vaults", async () => {
    let shown = false;
    const tree = (name: string) => json(200, { damaged: 0, root: { name, path: `/${name}`, folders: [], notes: [] } });
    const fetch = fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: shown, until: shown ? "2026-11-09T10:10:00Z" : null } }),
      "GET /api/vaults": () => json(200, shown ? [vault("Work"), vault("Tagebuch", true)] : [vault("Work")]),
      "GET /api/embeddings": () => json(200, []),
      "GET /api/vaults/Work/tree": () => tree("Work"),
      "GET /api/changes": () => json(200, []),
      "GET /api/member-notices": () => json(200, []),
      "POST /api/account/discreet/reveal": async (r) => {
        const body = (await r.clone().json()) as { pin: string };
        if (body.pin !== "2468") return json(400, { error: wrongPin });
        shown = true;
        return revealed("2026-11-09T10:10:00Z");
      },
    });
    renderApp("/notes/Work");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await userEvent.click(await screen.findByRole("button", { name: "Switch vault: Work" }));
    await userEvent.click(within(screen.getByRole("dialog", { name: "Vaults" })).getByRole("button", { name: "More vaults…" }));
    const dialog = await screen.findByRole("dialog", { name: "More vaults" });
    expect(useDiscreet.getState().revealed).toBe(false);

    await enter(dialog, "1357");
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(wrongPin);

    await enter(dialog, "2468");
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "More vaults" })).not.toBeInTheDocument());
    // The session and the vault list are asked again, and the store follows them.
    await waitFor(() => expect([...useDiscreet.getState().names]).toEqual(["Tagebuch"]));
    expect(useDiscreet.getState()).toMatchObject({ revealed: true, until: "2026-11-09T10:10:00Z" });
    expect(fetch.mock.calls.filter(([r]) => new URL((r as Request).url).pathname === "/api/session").length).toBeGreaterThan(1);
  });
});
