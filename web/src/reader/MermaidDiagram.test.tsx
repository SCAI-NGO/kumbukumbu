// SPDX-License-Identifier: AGPL-3.0-or-later
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import mermaid from "mermaid";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import MermaidDiagram from "./MermaidDiagram";

vi.mock("mermaid", () => ({ default: { initialize: vi.fn(), parse: vi.fn(), render: vi.fn() } }));

const m = vi.mocked(mermaid);

const flowchart = "flowchart LR\n  Sensor --> Controller --> Pump";
const svg = '<svg viewBox="0 0 640 120" width="100%" style="max-width: 640px;"><g/></svg>';

/** jsdom has no blob URLs: they are stubbed, and spied on. */
function blobUrls() {
  for (const name of ["createObjectURL", "revokeObjectURL"] as const) {
    if (!(name in URL)) Object.defineProperty(URL, name, { value: () => undefined, configurable: true, writable: true });
  }
  let n = 0;
  return {
    create: vi.spyOn(URL, "createObjectURL").mockImplementation(() => `blob:http://localhost/${n++}`),
    revoke: vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => undefined),
  };
}

const deferred = <T,>() => {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
};

type Rendered = Awaited<ReturnType<typeof mermaid.render>>;
const drawn = (text = svg) => ({ svg: text }) as Rendered;

let urls: ReturnType<typeof blobUrls>;

beforeEach(() => {
  urls = blobUrls();
  m.initialize.mockReset();
  m.parse.mockReset().mockResolvedValue({ diagramType: "flowchart-v2", config: {} });
  m.render.mockReset().mockResolvedValue(drawn());
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("MermaidDiagram", () => {
  it("draws the diagram as an image with its alt text", async () => {
    render(<MermaidDiagram source={flowchart} dark={false} />);
    expect(screen.getByText("Drawing the diagram …")).toBeInTheDocument();
    const img = await screen.findByRole("img", { name: "Diagram (flowchart)" });
    expect(img.getAttribute("src")).toMatch(/^blob:/);
    expect(screen.queryByText("Drawing the diagram …")).not.toBeInTheDocument();
    const blob = urls.create.mock.calls[0][0] as Blob;
    expect(blob.type).toBe("image/svg+xml");
    const text = await blob.text();
    expect(text).toContain('width="640"');
    expect(text).toContain('height="120"');
    expect(text).not.toContain('width="100%"');
    expect(text).not.toContain("max-width");
    expect(img.closest("a")).toBeNull();
  });

  it("gives the image well-formed XML from Mermaid's HTML serialisation", async () => {
    m.render.mockResolvedValue(
      drawn(
        '<svg viewBox="0 0 640 120" width="100%" style="max-width: 640px;"><g><text>Pump&nbsp;A</text>' +
          "<foreignObject><div>Sensor<br>Pump</div></foreignObject></g></svg>",
      ),
    );
    render(<MermaidDiagram source={flowchart} dark={false} />);
    await screen.findByRole("img");
    const text = await (urls.create.mock.calls[0][0] as Blob).text();
    const parsed = new DOMParser().parseFromString(text, "image/svg+xml");
    expect(parsed.getElementsByTagName("parsererror")).toHaveLength(0);
    expect(parsed.documentElement.getAttribute("width")).toBe("640");
    expect(parsed.documentElement.getAttribute("height")).toBe("120");
    expect(parsed.documentElement.hasAttribute("style")).toBe(false);
    expect(parsed.documentElement.textContent).toContain("Pump A");
    expect(text).not.toContain("&nbsp;");
  });

  it("an image that can't be shown shows the source and says so", async () => {
    render(<MermaidDiagram source={flowchart} dark={false} />);
    const img = await screen.findByRole("img");
    const url = img.getAttribute("src");
    fireEvent.error(img);
    expect(await screen.findByRole("note")).toHaveTextContent(/^This diagram can't be drawn: the image could not be shown\.$/);
    const code = document.querySelector("code.language-mermaid")!;
    expect(code).toBeVisible();
    expect(code.textContent).toBe(flowchart);
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
    expect(urls.revoke).toHaveBeenCalledWith(url);
  });

  it("uses accTitle and accDescr as the alt text", async () => {
    render(<MermaidDiagram source={"flowchart LR\n accTitle: Pump chain\n accDescr: Sensor to pump\n A-->B"} dark={false} />);
    expect(await screen.findByRole("img", { name: "Pump chain. Sensor to pump" })).toBeInTheDocument();
  });

  it("passes the theme", async () => {
    const { rerender } = render(<MermaidDiagram source={flowchart} dark />);
    const first = await screen.findByRole("img");
    const firstUrl = first.getAttribute("src");
    expect(m.initialize).toHaveBeenLastCalledWith(
      expect.objectContaining({ theme: "dark", securityLevel: "strict", htmlLabels: false, flowchart: expect.objectContaining({ htmlLabels: false }) }),
    );

    rerender(<MermaidDiagram source={flowchart} dark={false} />);
    await waitFor(() => expect(m.render).toHaveBeenCalledTimes(2));
    expect(m.initialize).toHaveBeenLastCalledWith(expect.objectContaining({ theme: "default" }));
    await waitFor(() => expect(screen.getByRole("img").getAttribute("src")).not.toBe(firstUrl));
    expect(urls.revoke).toHaveBeenCalledWith(firstUrl);
  });

  it("an init directive cannot loosen the security level", async () => {
    const source = '%%{init: {"securityLevel": "loose"}}%%\nflowchart LR\n A-->B';
    const { container } = render(<MermaidDiagram source={source} dark={false} />);
    expect(await screen.findByRole("img", { name: "Diagram (flowchart)" })).toBeInTheDocument();
    expect(m.initialize).toHaveBeenCalled();
    for (const [config] of m.initialize.mock.calls) expect(config.securityLevel).toBe("strict");
    expect(container.querySelectorAll("svg").length).toBe(0);
  });

  it("shows the source under Show source", async () => {
    render(<MermaidDiagram source={flowchart} dark={false} />);
    await screen.findByRole("img");
    const button = screen.getByRole("button", { name: "Show source" });
    expect(button).toHaveAttribute("aria-expanded", "false");
    const code = document.querySelector("code.language-mermaid")!;
    expect(code).not.toBeVisible();
    expect(button.getAttribute("aria-controls")).toBe(code.closest("pre")!.id);

    fireEvent.click(button);
    expect(screen.getByRole("button", { name: "Hide source" })).toHaveAttribute("aria-expanded", "true");
    expect(code).toBeVisible();
    expect(code.textContent).toBe(flowchart);
  });

  it("offers Full size only for an image wider than its frame", async () => {
    render(<MermaidDiagram source={flowchart} dark={false} />);
    const img = await screen.findByRole("img");
    expect(screen.queryByRole("button", { name: "Full size" })).not.toBeInTheDocument();

    Object.defineProperty(img, "naturalWidth", { value: 1200, configurable: true });
    Object.defineProperty(img.parentElement!, "clientWidth", { value: 600, configurable: true });
    fireEvent.load(img);
    const full = screen.getByRole("button", { name: "Full size" });
    expect(full).toHaveAttribute("aria-pressed", "false");
    expect(img).toHaveClass("max-w-full");

    fireEvent.click(full);
    expect(screen.getByRole("button", { name: "Fit" })).toHaveAttribute("aria-pressed", "true");
    expect(img).not.toHaveClass("max-w-full");
  });

  it("a new image starts fitted", async () => {
    const { rerender } = render(<MermaidDiagram source={flowchart} dark={false} />);
    const img = await screen.findByRole("img");
    Object.defineProperty(img, "naturalWidth", { value: 1200, configurable: true });
    Object.defineProperty(img.parentElement!, "clientWidth", { value: 600, configurable: true });
    fireEvent.load(img);
    fireEvent.click(screen.getByRole("button", { name: "Full size" }));
    const first = img.getAttribute("src");

    rerender(<MermaidDiagram source={flowchart} dark />);
    await waitFor(() => expect(screen.getByRole("img").getAttribute("src")).not.toBe(first));
    expect(screen.getByRole("img")).toHaveClass("max-w-full");
  });

  it("a parse error shows the source and the line", async () => {
    m.parse.mockRejectedValue(new Error("Parse error on line 2:\n...A-->\n-----^"));
    const source = "flowchart LR\n A-->";
    render(<MermaidDiagram source={source} dark={false} />);
    const note = await screen.findByRole("note");
    expect(note).toHaveTextContent(/^This diagram can't be drawn: Parse error on line 2:$/);
    const code = document.querySelector("code.language-mermaid")!;
    expect(code).toBeVisible();
    expect(code.textContent).toBe(source);
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
    expect(m.render).not.toHaveBeenCalled();
  });

  it("a failed render leaves nothing of Mermaid's in the document", async () => {
    m.render.mockImplementation(async (id) => {
      const div = document.createElement("div");
      div.id = `d${id}`;
      document.body.append(div);
      throw new Error("Edge limit exceeded");
    });
    render(<MermaidDiagram source={flowchart} dark={false} />);
    expect(await screen.findByRole("note")).toHaveTextContent("This diagram can't be drawn: Edge limit exceeded");
    const [id] = m.render.mock.calls[0];
    expect(document.getElementById(`d${id}`)).toBeNull();
  });

  it("renders diagrams one after another", async () => {
    const held = deferred<Rendered>();
    m.render.mockReturnValueOnce(held.promise);
    render(
      <>
        <MermaidDiagram source={flowchart} dark={false} />
        <MermaidDiagram source={"sequenceDiagram\n Alice->>Bob: Hi"} dark={false} />
      </>,
    );
    await waitFor(() => expect(m.render).toHaveBeenCalledTimes(1));
    await new Promise((r) => setTimeout(r, 20));
    expect(m.render).toHaveBeenCalledTimes(1);

    held.resolve(drawn());
    await waitFor(() => expect(m.render).toHaveBeenCalledTimes(2));
    expect(await screen.findByRole("img", { name: "Diagram (sequence)" })).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "Diagram (flowchart)" })).toBeInTheDocument();
  });

  it("revokes the image's URL when it goes", async () => {
    const { unmount } = render(<MermaidDiagram source={flowchart} dark={false} />);
    const url = (await screen.findByRole("img")).getAttribute("src");
    expect(urls.revoke).not.toHaveBeenCalled();
    unmount();
    expect(urls.revoke).toHaveBeenCalledWith(url);
  });

  it("a render that finishes after unmount makes no image", async () => {
    const held = deferred<Rendered>();
    m.render.mockReturnValueOnce(held.promise);
    const { unmount } = render(<MermaidDiagram source={flowchart} dark={false} />);
    await waitFor(() => expect(m.render).toHaveBeenCalledTimes(1));
    unmount();
    held.resolve(drawn());
    await new Promise((r) => setTimeout(r, 20));
    expect(urls.create).not.toHaveBeenCalled();
  });

  it("the config is secured against init directives", async () => {
    render(<MermaidDiagram source={flowchart} dark={false} />);
    await screen.findByRole("img");
    expect(m.initialize).toHaveBeenCalledWith(
      expect.objectContaining({
        secure: expect.arrayContaining(["securityLevel", "htmlLabels", "flowchart", "fontFamily", "themeCSS"]),
        suppressErrorRendering: true,
        startOnLoad: false,
      }),
    );
  });
});

describe("diagramAlt", () => {
  it("names the diagram's type", async () => {
    const { diagramAlt } = await import("./mermaidRender");
    expect(diagramAlt("graph TD\n A-->B")).toBe("Diagram (flowchart)");
    expect(diagramAlt("%% a comment\n\nsequenceDiagram\n Alice->>Bob: Hi")).toBe("Diagram (sequence)");
    expect(diagramAlt("stateDiagram-v2\n [*] --> Home")).toBe("Diagram (state)");
    expect(diagramAlt("xychart-beta\n x-axis [a, b]")).toBe("Diagram (xychart)");
    expect(diagramAlt("---\ntitle: Work\n---\nclassDiagram\n class Pump")).toBe("Diagram (class)");
  });

  it("takes a block accDescr", async () => {
    const { diagramAlt } = await import("./mermaidRender");
    expect(diagramAlt("flowchart LR\n accTitle: Pump chain\n accDescr {\n  Sensor to\n  pump\n }\n A-->B")).toBe("Pump chain. Sensor to pump");
    expect(diagramAlt("flowchart LR\n accDescr: Sensor to pump\n A-->B")).toBe("Sensor to pump");
  });
});
