// SPDX-License-Identifier: AGPL-3.0-or-later
// Settings, Offline (offline mode design §3a): the copy on this device, which vaults it keeps, the attachments and
// their budget, what is kept offline whole, the browser's storage, and the devices with a copy.
import { useMutation, useQuery } from "@tanstack/react-query";
import { attachmentKind, isMarker } from "eidolon-core/engine/paths";
import { attachmentsUsed, BUDGETS, mirrors } from "eidolon-core/store/choice";
import type { SealedMirror } from "eidolon-core/store/mirror";
import { useEffect, useId, useReducer, useState } from "react";
import { Link } from "react-router-dom";
import { errorText } from "../../api/error-text";
import { useVaults, type VaultView } from "../../api/notes";
import { storageText } from "../../api/storage";
import ConfirmTyped from "../../components/ConfirmTyped";
import { FormError, inputClass, secondaryButtonClass } from "../../components/form";
import { useOfflineCopy } from "../../offline/copy";
import { chooseOffline } from "../../offline/engines";
import { storageUse } from "../../offline/ports";
import { openCopy, turnOff, turnOn } from "../../offline/turnOn";
import { useSession } from "../../state/session";
import { isStandalone } from "../../update/InstallHint";
import { changes, Confirm, DeviceCopies, failureText, plural } from "./DeviceCopies";
import { Section } from "./Section";

/** The copy changes under the page (downloads, other tabs): read again every second while it is open. */
function useEverySecond(on: boolean) {
  const [, tick] = useReducer((n: number) => n + 1, 0);
  useEffect(() => {
    if (!on) return;
    const timer = setInterval(tick, 1000);
    return () => clearInterval(timer);
  }, [on]);
}

/** Changes the choice and shows it at once, rather than at the next second. */
function useChoose(mirror: SealedMirror) {
  const [, shown] = useReducer((n: number) => n + 1, 0);
  return (change: Parameters<typeof chooseOffline>[1]) => chooseOffline(mirror, change).finally(shown);
}

export default function Offline() {
  const user = useSession((s) => s.user);
  const header = useOfflineCopy((s) => s.header);
  const mirror = useOfflineCopy((s) => s.mirror);
  const open = mirror !== null && !mirror.locked ? mirror : null;
  useEverySecond(open !== null);
  // A copy kept from an earlier visit opens with the slot's key: signed in, nothing to ask.
  useEffect(() => {
    if (user && header === null) void openCopy(user.id).catch(() => {});
  }, [user, header]);
  return (
    <>
      <KeepSection mirror={open} />
      {open && <VaultsSection mirror={open} />}
      {open && <AttachmentsSection mirror={open} />}
      {open && <KeptSection mirror={open} />}
      {header && <StorageSection />}
      <DevicesSection />
      {header && <RemoveSection mirror={open} />}
    </>
  );
}

function KeepSection({ mirror }: { mirror: SealedMirror | null }) {
  const user = useSession((s) => s.user);
  const header = useOfflineCopy((s) => s.header);
  const [asking, setAsking] = useState<"on" | "off" | null>(null);
  const on = header !== null;
  return (
    <Section title="Offline">
      <label className="flex min-h-11 items-center gap-3 text-text">
        <span className="min-w-0 flex-1">Keep my notes on this device</span>
        <input
          type="checkbox"
          role="switch"
          checked={on}
          disabled={!user && !on}
          onChange={() => setAsking(on ? "off" : "on")}
          className="size-5 shrink-0 accent-accent"
        />
      </label>
      <p className="text-text-body">
        Your notes stay readable and writable here when you're offline. The copy is sealed with your password: someone who finds the device can only try to guess
        it.
      </p>
      {header && !mirror && <p className="text-muted">The copy on this device opens when you sign in.</p>}
      {asking === "on" && user && <TurnOnDialog user={user} onClose={() => setAsking(null)} />}
      {asking === "off" && header && <RemoveCopyDialog waiting={mirror?.waiting() ?? null} onClose={() => setAsking(null)} />}
    </Section>
  );
}

function RemoveSection({ mirror }: { mirror: SealedMirror | null }) {
  const [asking, setAsking] = useState(false);
  const waiting = mirror?.waiting() ?? 0;
  return (
    <Section title="Remove the copy from this device">
      <p className="text-text-body">
        {waiting > 0 ? `${changes(waiting)} haven't uploaded yet: they go with the copy.` : "Everything on this device is uploaded."}
      </p>
      <button type="button" onClick={() => setAsking(true)} className={`${secondaryButtonClass} self-start`}>
        Remove the copy
      </button>
      {asking && <RemoveCopyDialog waiting={mirror?.waiting() ?? null} onClose={() => setAsking(false)} />}
    </Section>
  );
}

function TurnOnDialog({ user, onClose }: { user: { id: string; username: string }; onClose(): void }) {
  const [password, setPassword] = useState("");
  const on = useMutation({ mutationFn: () => turnOn(user, password), onSuccess: onClose });
  return (
    <ConfirmTyped
      title="Keep your notes on this device"
      text="Your password seals the copy, and opens it when you're offline."
      action="Keep them here"
      pending={on.isPending}
      error={on.isError ? failureText(on.error) : null}
      password={password}
      onPasswordChange={setPassword}
      onConfirm={() => on.mutate()}
      onClose={onClose}
    />
  );
}

function RemoveCopyDialog({ waiting, onClose }: { waiting: number | null; onClose(): void }) {
  const header = useOfflineCopy((s) => s.header);
  const off = useMutation({ mutationFn: () => turnOff(header!), onSuccess: onClose });
  const lost = waiting !== null && waiting > 0 ? `${changes(waiting)} haven't uploaded yet and will be lost. ` : "";
  return (
    <Confirm
      title="Remove the copy from this device?"
      text={`${lost}Your notes stay in Kumbu²; this device no longer has them offline.`}
      action="Remove"
      busy={off.isPending}
      error={off.isError ? failureText(off.error) : null}
      onConfirm={() => off.mutate()}
      onClose={onClose}
    />
  );
}

/** How much of a vault is here: its notes while they come, then with its attachments' size. */
function vaultDetail(mirror: SealedMirror, vault: VaultView): string {
  const held = mirror.vault(vault.id);
  if (held === undefined) return plural(Number(vault.notes), "note", "notes");
  const notes = held.list().filter((e) => e.remote !== undefined && attachmentKind(e.path) === null && !isMarker(e.path));
  const here = notes.filter((e) => e.local !== undefined).length;
  if (here < notes.length) return `${here.toLocaleString("en-GB")} of ${plural(notes.length, "note", "notes")}`;
  return `${plural(notes.length, "note", "notes")} · ${storageText(attachmentsUsed([held]))} of attachments`;
}

function VaultsSection({ mirror }: { mirror: SealedMirror }) {
  const vaults = useVaults();
  const choice = mirror.choice;
  const [dropping, setDropping] = useState<{ vault: VaultView; waiting: number } | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Never a discreet vault, shown or not (§4.3); nor one still being set up.
  const listed = vaults.data?.filter((v) => !v.discreet && !v.pending) ?? [];
  const chosen = useChoose(mirror);
  const choose = (change: Parameters<typeof chooseOffline>[1]) => chosen(change).catch((e: unknown) => setError(failureText(e)));
  const set = (vault: VaultView, keep: boolean) => {
    const waiting = mirror.vault(vault.id)?.waiting() ?? 0;
    if (!keep && waiting > 0 && dropping === null) return setDropping({ vault, waiting });
    setDropping(null);
    void choose((c) => {
      const ids = c.all ? listed.map((v) => v.id) : c.vaults;
      return { ...c, all: false, vaults: keep ? [...new Set([...ids, vault.id])] : ids.filter((id) => id !== vault.id) };
    });
  };
  return (
    <Section title="Vaults on this device">
      <label className="flex min-h-11 items-center gap-3 text-text">
        <span className="min-w-0 flex-1">All my vaults</span>
        <input
          type="checkbox"
          checked={choice.all}
          // Off, the vaults kept now stay chosen one by one: nothing goes until a vault is unticked.
          onChange={(e) => {
            const all = e.target.checked;
            void choose((c) => ({ ...c, all, vaults: all ? [] : listed.map((v) => v.id) }));
          }}
          className="size-5 shrink-0 accent-accent"
        />
      </label>
      <p className="text-sm text-muted">New vaults, and vaults shared with you later, follow All my vaults.</p>
      {vaults.isError && <FormError>{errorText(vaults.error)}</FormError>}
      <ul aria-label="Vaults on this device" className="flex flex-col gap-1">
        {listed.map((vault) => (
          <li key={vault.id}>
            <label className="flex min-h-11 items-center gap-3 text-text">
              <span className="flex min-w-0 flex-1 flex-col">
                <span className="break-words">{vault.name}</span>
                <span className="text-sm text-muted">{vaultDetail(mirror, vault)}</span>
              </span>
              <input
                type="checkbox"
                aria-label={`Keep ${vault.name} on this device`}
                checked={mirrors(choice, vault.id)}
                onChange={(e) => set(vault, e.target.checked)}
                className="size-5 shrink-0 accent-accent"
              />
            </label>
          </li>
        ))}
      </ul>
      {error && <FormError>{error}</FormError>}
      {dropping && (
        <Confirm
          title={`Stop keeping ${dropping.vault.name} here?`}
          text={`${changes(dropping.waiting)} in it haven't uploaded yet and will be lost.`}
          action="Stop keeping it"
          busy={false}
          error={null}
          onConfirm={() => set(dropping.vault, false)}
          onClose={() => setDropping(null)}
        />
      )}
    </Section>
  );
}

function AttachmentsSection({ mirror }: { mirror: SealedMirror }) {
  const id = useId();
  const choice = mirror.choice;
  const choose = useChoose(mirror);
  return (
    <Section title="Attachments">
      <p className="text-text-body">
        Images and PDFs up to 1 MB come with the notes. Larger ones are kept once you open them online, up to the space below; the ones opened longest ago make
        room first.
      </p>
      <p className="text-text">Attachments here: {storageText(attachmentsUsed(mirror.held()))}</p>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={id} className="text-text">
          Space for opened attachments
        </label>
        <select
          id={id}
          value={choice.budget}
          onChange={(e) => {
            // Read now: the change runs later, once the copy's lock is held.
            const budget = Number(e.target.value);
            void choose((c) => ({ ...c, budget }));
          }}
          className={inputClass}
        >
          {BUDGETS.map((b) => (
            <option key={b} value={b}>
              {storageText(b)}
            </option>
          ))}
        </select>
      </div>
    </Section>
  );
}

function KeptSection({ mirror }: { mirror: SealedMirror }) {
  const vaults = useVaults();
  const choose = useChoose(mirror);
  const kept = Object.entries(mirror.choice.kept).flatMap(([vault, folders]) => folders.map((folder) => ({ vault, folder })));
  const name = (id: string) => vaults.data?.find((v) => v.id === id)?.name ?? "A vault";
  return (
    <Section title="Kept offline">
      <p className="text-text-body">Every attachment in these folders stays on this device, whatever its size. Choose Keep offline in a folder's or vault's menu.</p>
      {kept.length === 0 && <p className="text-muted">Nothing is kept offline whole.</p>}
      {kept.length > 0 && (
        <ul aria-label="Kept offline" className="flex flex-col gap-1">
          {kept.map(({ vault, folder }) => (
            <li key={`${vault}/${folder}`} className="flex min-h-11 items-center gap-3 text-text">
              <span className="min-w-0 flex-1 break-words">{folder === "" ? name(vault) : `${name(vault)} / ${folder}`}</span>
              <button
                type="button"
                onClick={() =>
                  void choose((c) => ({ ...c, kept: { ...c.kept, [vault]: (c.kept[vault] ?? []).filter((f) => f !== folder) } }))
                }
                className={secondaryButtonClass}
              >
                Remove
              </button>
            </li>
          ))}
        </ul>
      )}
    </Section>
  );
}

const isSafari = () => /Safari\//.test(navigator.userAgent) && !/Chrome\/|Chromium\/|Edg\/|Firefox\//.test(navigator.userAgent);

function StorageSection() {
  const use = useQuery({ queryKey: ["offline-storage"], queryFn: storageUse, refetchInterval: 5000 });
  return (
    <Section title="Storage">
      {use.data?.usage != null && (
        <p className="text-text">
          Uses {storageText(use.data.usage)}
          {use.data.quota != null && ` of about ${storageText(use.data.quota)} this browser allows`}
        </p>
      )}
      {use.data && (
        <p className="text-text-body">
          {use.data.persisted ? "This browser keeps the copy when it clears space." : "This browser may remove the copy when it clears space."}
        </p>
      )}
      {isSafari() && !isStandalone() && (
        <p className="text-text-body">
          Safari removes the copy after 7 days without a visit. <Link to="/settings/appearance#install" className="text-accent underline">Add Kumbu² to your Home Screen</Link> to
          keep it.
        </p>
      )}
    </Section>
  );
}

function DevicesSection() {
  return (
    <Section title="Devices with a copy">
      <DeviceCopies />
    </Section>
  );
}

