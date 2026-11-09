// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { fakeApi } from "../test/app";
import TextFile from "./TextFile";

afterEach(() => {
  cleanup();
  delete (window as unknown as { hit?: number }).hit;
});

const text = (body: string) => new Response(body, { headers: { "Content-Type": "text/plain; charset=utf-8" } });

describe("TextFile", () => {
  it("shows an HTML file as its text, never rendered", async () => {
    const body = "<script>window.hit=1</script><b>x</b>";
    fakeApi({ "GET /api/files/Work/_skills/x/index.html": () => text(body) });
    render(<TextFile vault="Work" path="_skills/x/index.html" />);
    expect(await screen.findByText(body)).toBeInTheDocument();
    expect(document.querySelector("b")).toBeNull();
    expect(document.querySelector("script")).toBeNull();
    expect(document.querySelector("iframe, object, embed")).toBeNull();
    expect((window as unknown as { hit?: number }).hit).toBeUndefined();
  });

  it("shows an SVG file as its text", async () => {
    const body = '<svg xmlns="http://www.w3.org/2000/svg"><script>window.hit=1</script></svg>';
    fakeApi({ "GET /api/files/Work/_skills/x/logo.svg": () => text(body) });
    render(<TextFile vault="Work" path="_skills/x/logo.svg" />);
    expect(await screen.findByText(body)).toBeInTheDocument();
    expect(document.querySelector("pre svg, pre script")).toBeNull();
    expect((window as unknown as { hit?: number }).hit).toBeUndefined();
  });

  it("offers a download", async () => {
    fakeApi({ "GET /api/files/Work/_skills/x/run.py": () => text("print(1)\n") });
    render(<TextFile vault="Work" path="_skills/x/run.py" />);
    await screen.findByText(/print\(1\)/);
    expect(screen.getByRole("link", { name: /download/i })).toHaveAttribute("href", "/api/files/Work/_skills/x/run.py?download=1");
  });

  it("says so when the file can't be read", async () => {
    fakeApi({ "GET /api/files/Work/_skills/x/run.py": () => new Response("no", { status: 500 }) });
    render(<TextFile vault="Work" path="_skills/x/run.py" />);
    expect(await screen.findByRole("alert")).toHaveTextContent(/couldn't be read/i);
  });
});
