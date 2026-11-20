// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it } from "vitest";
import { useDiscreet } from "./state";

afterEach(() => {
  useDiscreet.getState().reset();
  useDiscreet.setState({ gone: new Set() });
});

describe("the discreet mode's state", () => {
  it("a vault hidden again and then listed as ordinary is no longer gone", () => {
    useDiscreet.setState({ gone: new Set(["Tagebuch"]) });
    useDiscreet.getState().setFromVaults([{ name: "Tagebuch", discreet: true }]);
    expect(useDiscreet.getState().gone.has("Tagebuch")).toBe(true);
    useDiscreet.getState().setFromVaults([{ name: "Tagebuch", discreet: false }]);
    expect(useDiscreet.getState().gone.has("Tagebuch")).toBe(false);
  });

  it("keeps the discreet vaults the list named while shown, for hiding again, until they are listed as ordinary or hidden", () => {
    const { setFromVaults } = useDiscreet.getState();
    setFromVaults([{ name: "Work" }, { name: "Tagebuch", discreet: true }]);
    setFromVaults([{ name: "Work" }]);
    expect([...useDiscreet.getState().names]).toEqual([]);
    expect([...useDiscreet.getState().listed]).toEqual(["Tagebuch"]);

    setFromVaults([{ name: "Work" }, { name: "Tagebuch", discreet: false }]);
    expect([...useDiscreet.getState().listed]).toEqual([]);

    setFromVaults([{ name: "Work" }, { name: "Tagebuch", discreet: true }]);
    useDiscreet.getState().reset();
    expect([...useDiscreet.getState().listed]).toEqual([]);
  });
});
