// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, type ReactNode } from "react";

export function Section({ title, id: anchor, children }: { title: string; id?: string; children: ReactNode }) {
  const id = useId();
  return (
    <section id={anchor} aria-labelledby={id} className="flex flex-col gap-4 rounded-[14px] border border-line bg-surface p-4">
      <h2 id={id} className="text-lg font-bold text-text">
        {title}
      </h2>
      {children}
    </section>
  );
}

export function Done({ children }: { children: ReactNode }) {
  return (
    <p role="status" className="text-text">
      {children}
    </p>
  );
}
