// SPDX-License-Identifier: AGPL-3.0-or-later
import { renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { HttpError } from "../api/client";
import { NO_SUCH_NOTE, useErrorTextFor } from "./errorText";
import { useDiscreet } from "./state";

afterEach(() => {
  useDiscreet.getState().reset();
  useDiscreet.setState({ gone: new Set() });
});

const missing = new HttpError(404, "There is no note at /Tagebuch/Fehlt.md (if it was removed, it is in the trash).");
const text = (path: string, error: unknown) => renderHook(() => useErrorTextFor(path)).result.current(error);

describe("the error text of a note's request", () => {
  it("is the sentence without its path for a 404 in a discreet vault, shown or hidden again", () => {
    useDiscreet.setState({ names: new Set(["Tagebuch"]) });
    expect(text("/Tagebuch/Fehlt.md", missing)).toBe(NO_SUCH_NOTE);
    useDiscreet.setState({ names: new Set(), gone: new Set(["Tagebuch"]) });
    expect(text("/Tagebuch/Fehlt.md", missing)).toBe(NO_SUCH_NOTE);
  });

  it("is the server's sentence for an ordinary vault, and for anything but a 404", () => {
    useDiscreet.setState({ names: new Set(["Tagebuch"]) });
    expect(text("/Work/Fehlt.md", new HttpError(404, "There is no note at /Work/Fehlt.md."))).toBe("There is no note at /Work/Fehlt.md.");
    expect(text("/Tagebuch/a.md", new HttpError(409, "The note changed."))).toBe("The note changed.");
  });
});
