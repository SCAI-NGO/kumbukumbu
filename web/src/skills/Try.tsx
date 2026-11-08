// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { useId, useState, type FormEvent } from "react";
import { api, call } from "../api/client";
import { errorText } from "../api/error-text";
import { useFindSkills } from "../api/skills";
import { FormError, inputClass, labelClass, primaryButtonClass } from "../components/form";

/** What `skills find` returns for a request, optionally as one of her agent keys or connections would see it. */
export default function Try() {
  const id = useId();
  const [text, setText] = useState("");
  const [asked, setAsked] = useState("");
  const [as, setAs] = useState("");
  const keys = useQuery({ queryKey: ["keys"], queryFn: () => call(api.GET("/api/keys")) });
  const connections = useQuery({ queryKey: ["connections"], queryFn: () => call(api.GET("/api/connections")) });
  const hits = useFindSkills(asked, as || undefined);
  const credentials = [
    ...(keys.data ?? []).filter((k) => k.kind === "agent").map((k) => ({ id: k.id, name: k.name })),
    ...(connections.data ?? []).map((c) => ({ id: c.id, name: c.name })),
  ];

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setAsked(text.trim());
  };

  return (
    <section aria-labelledby={`${id}-title`} className="flex min-w-0 flex-col gap-4">
      <h2 id={`${id}-title`} className="text-[22px] font-bold text-text">
        Try
      </h2>
      <form onSubmit={submit} className="flex flex-col gap-3">
        <label htmlFor={`${id}-q`} className={labelClass}>
          What an agent asks for
        </label>
        <input id={`${id}-q`} value={text} onChange={(e) => setText(e.target.value)} className={inputClass} />
        {credentials.length > 0 && (
          <>
            <label htmlFor={`${id}-as`} className={labelClass}>
              As seen by
            </label>
            <select id={`${id}-as`} value={as} onChange={(e) => setAs(e.target.value)} className={inputClass}>
              <option value="">All my vaults</option>
              {credentials.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </>
        )}
        <button type="submit" disabled={text.trim() === ""} className={primaryButtonClass}>
          Find
        </button>
      </form>
      {hits.isError && <FormError>{errorText(hits.error)}</FormError>}
      {hits.data?.length === 0 && <p className="text-text-body">No skill fits.</p>}
      {hits.data && hits.data.length > 0 && (
        <ol className="flex flex-col gap-3">
          {hits.data.map((h) => (
            <li key={`${h.skill.location}/${h.skill.path ?? h.skill.name}`} className="flex min-w-0 flex-col gap-1 rounded-[12px] border border-line p-3">
              <h3 className="font-bold break-words text-text">
                {h.rank}. {h.skill.name}
              </h3>
              <p className="break-words whitespace-pre-wrap text-text-body">{h.skill.description}</p>
              <p className="font-mono text-xs text-muted">
                {h.skill.location === "system" ? "System" : h.skill.location} · score {Number(h.score).toFixed(3)}
              </p>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
