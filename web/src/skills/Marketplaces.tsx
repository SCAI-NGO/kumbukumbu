// SPDX-License-Identifier: AGPL-3.0-or-later
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useId, useState, type FormEvent } from "react";
import { errorText } from "../api/error-text";
import { addMarketplace, removeMarketplace, useMarketplaceListing, useMarketplaces, type MarketplaceListingView } from "../api/skills";
import { FormError, inputClass, labelClass, primaryButtonClass, secondaryButtonClass } from "../components/form";
import InstallDialog from "./InstallDialog";

/**
 * Her marketplaces (skills design §3.9): GitHub repositories added as owner/repo, browsed on demand and their skills
 * installed after a look at what they bring. The tab exists only where the admin has marketplaces on.
 */
export default function Marketplaces() {
  const id = useId();
  const queryClient = useQueryClient();
  const marketplaces = useMarketplaces();
  const [repo, setRepo] = useState("");
  const [browsing, setBrowsing] = useState<string | null>(null);
  const refresh = () => queryClient.invalidateQueries({ queryKey: ["skills", "marketplaces"] });
  const add = useMutation({
    mutationFn: () => addMarketplace(repo.trim()),
    onSuccess: async () => {
      setRepo("");
      await refresh();
    },
  });
  const remove = useMutation({
    mutationFn: removeMarketplace,
    onSuccess: async (_, removed) => {
      if (browsing === removed) setBrowsing(null);
      await refresh();
    },
  });

  if (marketplaces.isPending) return <p className="text-text-body">Loading…</p>;
  if (marketplaces.isError) return <FormError>{errorText(marketplaces.error)}</FormError>;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (repo.trim()) add.mutate();
  };
  return (
    <>
      <p className="text-text-body">A marketplace is a GitHub repository that offers skills. Kumbu² reads it only when you browse or install; nothing is updated by itself.</p>
      <form onSubmit={submit} className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-repo`} className={labelClass}>
          Repository
        </label>
        <div className="flex gap-2">
          <input
            id={`${id}-repo`}
            value={repo}
            onChange={(e) => setRepo(e.target.value)}
            placeholder="owner/repo"
            autoCapitalize="none"
            autoCorrect="off"
            spellCheck={false}
            className={`${inputClass} min-w-0 flex-1`}
          />
          <button type="submit" disabled={add.isPending || !repo.trim()} className={primaryButtonClass}>
            Add
          </button>
        </div>
      </form>
      {add.isError && <FormError>{errorText(add.error)}</FormError>}
      {remove.isError && <FormError>{errorText(remove.error)}</FormError>}
      {marketplaces.data.marketplaces.length === 0 ? (
        <p className="text-text-body">No marketplace yet.</p>
      ) : (
        <ul className="flex flex-col gap-3">
          {marketplaces.data.marketplaces.map((m) => (
            <li key={m.repo} className="flex min-w-0 flex-col gap-3 rounded-[12px] border border-line p-3">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <h3 className="font-mono text-sm font-bold break-all text-text">{m.repo}</h3>
                <div className="flex gap-2">
                  <button
                    type="button"
                    aria-label={`${browsing === m.repo ? "Close" : "Browse"} ${m.repo}`}
                    aria-expanded={browsing === m.repo}
                    onClick={() => setBrowsing(browsing === m.repo ? null : m.repo)}
                    className={secondaryButtonClass}
                  >
                    {browsing === m.repo ? "Close" : "Browse"}
                  </button>
                  <button type="button" disabled={remove.isPending} onClick={() => remove.mutate(m.repo)} className={secondaryButtonClass}>
                    Remove
                  </button>
                </div>
              </div>
              {browsing === m.repo && <Listing repo={m.repo} />}
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

function Listing({ repo }: { repo: string }) {
  const listing = useMarketplaceListing(repo);
  if (listing.isPending) return <p className="text-text-body">Loading…</p>;
  if (listing.isError) return <FormError>{errorText(listing.error)}</FormError>;
  return <Offer data={listing.data} />;
}

function Offer({ data }: { data: MarketplaceListingView }) {
  const [installing, setInstalling] = useState<{ path: string; name: string } | null>(null);
  const [installed, setInstalled] = useState<string | null>(null);
  const empty = data.skills.length === 0 && data.plugins.length === 0;
  return (
    <div className="flex flex-col gap-2">
      {data.note && <p className="text-text-body">{data.note}</p>}
      {empty && <p className="text-text-body">This repository offers no skills.</p>}
      {installed && (
        <p role="status" className="text-text">
          Installed to {installed}.
        </p>
      )}
      <ul className="flex flex-col gap-2">
        {data.skills.map((s) => (
          <li key={s.path} className="flex min-w-0 flex-col gap-1 border-t border-line pt-2">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h4 className="font-mono text-sm font-bold break-words text-text">{s.name}</h4>
              {s.installable && (
                <button type="button" onClick={() => setInstalling({ path: s.path, name: s.name })} className={secondaryButtonClass}>
                  Install…
                </button>
              )}
            </div>
            {s.description && <p className="text-text-body">{s.description}</p>}
            {s.plugin && <p className="text-sm text-muted">Plugin {s.plugin}</p>}
            {!s.installable && <p className="text-text-body">Not installable{s.reason ? `: ${s.reason}` : ""}.</p>}
            {s.warnings.map((w) => (
              <p key={w} className="text-sm text-muted">
                {w}
              </p>
            ))}
          </li>
        ))}
        {data.plugins.map((p) => (
          <li key={`plugin:${p.name}`} className="flex min-w-0 flex-col gap-1 border-t border-line pt-2">
            <h4 className="font-mono text-sm font-bold break-words text-text">{p.name}</h4>
            <p className="text-text-body">
              Plugin in another repository, not installable here{p.source ? `: ${p.source}` : ""}. Add that repository as a marketplace to install its skills.
            </p>
          </li>
        ))}
      </ul>
      {installing && (
        <InstallDialog
          repo={data.repo}
          commit={data.commit}
          path={installing.path}
          name={installing.name}
          onClose={() => setInstalling(null)}
          onInstalled={(path) => {
            setInstalling(null);
            setInstalled(path);
          }}
        />
      )}
    </div>
  );
}
