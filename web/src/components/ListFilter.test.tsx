// SPDX-License-Identifier: AGPL-3.0-or-later
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { useListFilter } from "./ListFilter";

type Thing = { n: string; s: string };
const things: Thing[] = [
  { n: "Anna", s: "open" },
  { n: "Bob", s: "used" },
  { n: "ÄRGER", s: "open" },
];
const statuses = [
  { value: "open", label: "Open" },
  { value: "used", label: "Used" },
  { value: "revoked", label: "Revoked" },
];

function Probe({ items }: { items: Thing[] }) {
  const { visible, bar } = useListFilter(items, {
    name: (t) => t.n,
    status: (t) => t.s,
    statuses,
    searchLabel: "Filter by name",
    noun: "things",
  });
  return (
    <>
      {bar}
      <ul>
        {visible.map((t) => (
          <li key={t.n}>{t.n}</li>
        ))}
      </ul>
    </>
  );
}

const listed = () => screen.queryAllByRole("listitem").map((li) => li.textContent);

describe("useListFilter", () => {
  it("shows everything and no summary at first", () => {
    render(<Probe items={things} />);
    expect(listed()).toEqual(["Anna", "Bob", "ÄRGER"]);
    expect(screen.getByRole("radio", { name: "All 3" })).toBeChecked();
    expect(screen.queryByText(/Showing/)).toBeNull();
  });

  it("matches the name ignoring case", async () => {
    render(<Probe items={things} />);
    const input = screen.getByRole("searchbox", { name: "Filter by name" });
    await userEvent.type(input, "anna");
    expect(listed()).toEqual(["Anna"]);
    expect(screen.getByText("Showing 1 of 3")).toBeInTheDocument();
    await userEvent.clear(input);
    await userEvent.type(input, "ärger");
    expect(listed()).toEqual(["ÄRGER"]);
  });

  it("treats blank text as no filter", async () => {
    render(<Probe items={things} />);
    await userEvent.type(screen.getByRole("searchbox"), "   ");
    expect(listed()).toHaveLength(3);
    expect(screen.queryByText(/Showing/)).toBeNull();
  });

  it("counts follow the text filter", async () => {
    render(<Probe items={things} />);
    await userEvent.type(screen.getByRole("searchbox"), "n");
    for (const name of ["All 1", "Open 1", "Used 0", "Revoked 0"]) {
      expect(screen.getByRole("radio", { name })).toBeInTheDocument();
    }
  });

  it("filters by status and keeps empty statuses choosable", async () => {
    render(<Probe items={things} />);
    await userEvent.click(screen.getByRole("radio", { name: "Used 1" }));
    expect(listed()).toEqual(["Bob"]);
    await userEvent.click(screen.getByRole("radio", { name: "Revoked 0" }));
    expect(screen.getByText("No things match.")).toBeInTheDocument();
    expect(listed()).toEqual([]);
  });

  it("Show all clears both filters", async () => {
    render(<Probe items={things} />);
    const input = screen.getByRole("searchbox");
    await userEvent.type(input, "bob");
    await userEvent.click(screen.getByRole("radio", { name: "Used 1" }));
    await userEvent.click(screen.getByRole("button", { name: "Show all" }));
    expect(listed()).toEqual(["Anna", "Bob", "ÄRGER"]);
    expect(input).toHaveValue("");
    expect(screen.getByRole("radio", { name: "All 3" })).toBeChecked();
  });

  it("keeps the filter when the items change", async () => {
    const { rerender } = render(<Probe items={things} />);
    await userEvent.click(screen.getByRole("radio", { name: "Used 1" }));
    rerender(<Probe items={things.map((t) => (t.n === "Bob" ? { ...t, s: "revoked" } : t))} />);
    expect(screen.getByRole("radio", { name: "Used 0" })).toBeChecked();
    expect(listed()).toEqual([]);
    expect(screen.getByText("No things match.")).toBeInTheDocument();
  });

  it("renders no bar for an empty list", () => {
    render(<Probe items={[]} />);
    expect(screen.queryByRole("searchbox")).toBeNull();
  });
});
