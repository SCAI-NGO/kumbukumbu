// SPDX-License-Identifier: AGPL-3.0-or-later
import { useId, type ReactNode } from "react";
import { Link } from "react-router-dom";

/** The frame of the screens before the app: sign-in, join, forgot password. */
export function AuthScreen({ title, lead, children }: { title: string; lead?: ReactNode; children?: ReactNode }) {
  return (
    <main className="mx-auto flex min-h-dvh w-full max-w-md flex-col justify-center gap-6 px-4 py-10">
      <header>
        <p className="font-mono text-[11px] uppercase tracking-[0.08em] text-accent-text">Kumbu²</p>
        <h1 className="mt-2 text-[28px] leading-tight font-bold text-text">{title}</h1>
        {lead && <p className="mt-2 text-text-body">{lead}</p>}
      </header>
      {children}
    </main>
  );
}

/** The frame of the account pages behind the drawer's account row: keys, settings, admin. */
export function AccountScreen({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <main className="mx-auto flex min-h-dvh w-full max-w-xl flex-col gap-6 px-4 py-10">
      <header className="flex flex-col gap-2">
        <Link to="/" className="flex min-h-11 items-center self-start text-accent-text">
          Back to the notes
        </Link>
        <h1 className="text-[28px] leading-tight font-bold text-text">{title}</h1>
      </header>
      {children}
    </main>
  );
}

export const labelClass = "font-mono text-[11px] uppercase tracking-[0.08em] text-muted";
// 16 px text so iOS doesn't zoom in; 48 px high for the thumb.
export const inputClass =
  "h-12 w-full rounded-[10px] border border-line bg-surface px-3 text-base text-text placeholder:text-dim focus:border-accent focus:outline-none";
export const primaryButtonClass =
  "min-h-12 w-full rounded-[12px] bg-accent px-4 font-medium text-on-accent disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent";
export const secondaryButtonClass =
  "min-h-11 rounded-[10px] border border-line bg-surface-2 px-4 text-text disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent";

export function TextField(props: {
  label: string;
  value: string;
  onChange(value: string): void;
  autoComplete: string;
  mono?: boolean;
}) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className={labelClass}>
        {props.label}
      </label>
      <input
        id={id}
        value={props.value}
        onChange={(e) => props.onChange(e.target.value)}
        autoComplete={props.autoComplete}
        autoCapitalize="none"
        autoCorrect="off"
        spellCheck={false}
        required
        className={`${inputClass}${props.mono ? " font-mono" : ""}`}
      />
    </div>
  );
}

export function FormError({ children }: { children: ReactNode }) {
  return (
    <p role="alert" className="rounded-[10px] border border-line bg-surface-2 px-3 py-2 text-text">
      {children}
    </p>
  );
}
