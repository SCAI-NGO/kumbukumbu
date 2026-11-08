// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, expect, it } from "vitest";
import { keepStrayDrops } from "./strayDrops";

let release = () => {};
afterEach(() => release());

function dragEvent(type: string, types: string[]) {
  const event = new Event(type, { bubbles: true, cancelable: true });
  Object.defineProperty(event, "dataTransfer", { value: { types, dropEffect: "" } });
  return event as DragEvent;
}

it("keeps files dropped anywhere from opening in place of the app", () => {
  release = keepStrayDrops();
  const over = dragEvent("dragover", ["Files"]);
  const drop = dragEvent("drop", ["Files"]);
  document.body.dispatchEvent(over);
  document.body.dispatchEvent(drop);
  expect(over.defaultPrevented).toBe(true);
  expect(drop.defaultPrevented).toBe(true);
  expect(drop.dataTransfer!.dropEffect).toBe("none");
});

it("leaves dragged text to land where it is dropped", () => {
  release = keepStrayDrops();
  const drop = dragEvent("drop", ["text/plain"]);
  document.body.dispatchEvent(drop);
  expect(drop.defaultPrevented).toBe(false);
});

it("leaves a drop a target took alone", () => {
  release = keepStrayDrops();
  const drop = dragEvent("drop", ["Files"]);
  drop.preventDefault();
  document.body.dispatchEvent(drop);
  expect(drop.dataTransfer!.dropEffect).toBe("");
});
