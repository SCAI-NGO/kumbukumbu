// SPDX-License-Identifier: AGPL-3.0-or-later
import { useQuery } from "@tanstack/react-query";
import { useEffect, useState, type ReactNode } from "react";
import { Link, useLocation, useNavigate } from "react-router-dom";
import { HttpError } from "../api/client";
import { errorText } from "../api/error-text";
import { useVaults } from "../api/notes";
import { AuthScreen, primaryButtonClass } from "../components/form";
import { useSession, type SessionView } from "../state/session";
import TargetDialog from "./TargetDialog";

/**
 * Where a skill link's code waits while its holder signs in: this tab's session storage, and nowhere else. Never in a
 * path, a query or the return address after sign-in, which would reach the server's and proxies' logs.
 */
export const skillCodeKey = "kumbu.skillCode";

/** The code after `#`, or "" for none or a garbled one (a cut-off escape such as `%E0`). */
function fragmentCode(hash: string) {
  try {
    return decodeURIComponent(hash.slice(1));
  } catch {
    return "";
  }
}

/** The session, or null without one. Asked past `api`, whose 401 would send the page to sign-in at once: she first reads why. */
async function sessionOrNull(): Promise<SessionView | null> {
  const response = await globalThis.fetch(new Request(`${window.location.origin}/api/session`, { credentials: "include" }));
  if (response.status === 401) return null;
  if (!response.ok) throw new HttpError(response.status, "");
  return (await response.json()) as SessionView;
}

/**
 * Add to my skills, `/skills/add#<code>` (skills design §3.12, Decision 6), the way in from a shared skill's page: the
 * code is moved from the fragment into this tab's session storage and the address becomes plain `/skills/add`, also
 * across signing in, as `/share` does. Signed in, she picks the target (Skills preselected, or `_skills/` of any vault
 * she may write); the code is forgotten only once the copy is made, so a taken name leaves it for another try.
 */
export default function AddSkill() {
  const location = useLocation();
  const navigate = useNavigate();
  const [code, setCode] = useState(() => {
    const fromLink = fragmentCode(location.hash);
    if (fromLink) sessionStorage.setItem(skillCodeKey, fromLink);
    return fromLink || sessionStorage.getItem(skillCodeKey) || "";
  });

  // A link opened in a tab that already holds one replaces its code.
  useEffect(() => {
    if (!location.hash) return;
    const fromLink = fragmentCode(location.hash);
    if (fromLink && fromLink !== code) {
      sessionStorage.setItem(skillCodeKey, fromLink);
      setCode(fromLink);
    }
    navigate({ pathname: "/skills/add" }, { replace: true });
  }, [location.hash, navigate, code]);

  const signedIn = useSession((s) => s.signedIn);
  const session = useQuery({ queryKey: ["skill-add-session"], queryFn: sessionOrNull, gcTime: 0 });
  useEffect(() => {
    if (session.data) signedIn(session.data);
  }, [session.data, signedIn]);

  if (!code) return <AddScreen title="Add a shared skill" lead="This link has no skill in it. Open the full link you were sent." />;
  if (session.isPending) return <AddScreen title="Add a shared skill" />;
  if (session.isError) return <AddScreen title="Add a shared skill" lead={errorText(session.error)} />;
  if (!session.data) {
    return (
      <AddScreen title="Add a shared skill" lead="Someone shared a skill with you on this Kumbu².">
        <p className="text-text-body">
          To add a copy to your skills, sign in first: it needs an account on this server, which only an admin makes. Without one, the page you came from
          offers the skill as a .zip.
        </p>
        <Link to={`/signin?next=${encodeURIComponent("/skills/add")}`} className={`${primaryButtonClass} flex items-center justify-center`}>
          Sign in
        </Link>
      </AddScreen>
    );
  }
  return <Target key={code} code={code} />;
}

/** The frame of this page, with a way out on each screen. */
function AddScreen({ title, lead, children }: { title: string; lead?: ReactNode; children?: ReactNode }) {
  return (
    <AuthScreen title={title} lead={lead}>
      {children}
      <Link to="/skills/installed" className="flex min-h-11 items-center self-start text-accent-text">
        To your skills
      </Link>
    </AuthScreen>
  );
}

function Target({ code }: { code: string }) {
  const navigate = useNavigate();
  const vaults = useVaults();
  const [added, setAdded] = useState<string | null>(null);

  if (added) {
    const at = added.split("/").filter(Boolean);
    return <AddScreen title="Added" lead={`${at.at(-1)} is in ${at[0]} now. It stays as it is; you'll be told when there's a newer version.`} />;
  }
  return (
    <>
      <AddScreen title="Add a shared skill" lead="Choose where the copy goes." />
      <TargetDialog
        mode="add"
        code={code}
        vaults={vaults.data}
        onClose={() => navigate("/skills/installed", { replace: true })}
        onAdded={(path) => {
          // Only now: until the copy is made, the code must stay for another try.
          if (sessionStorage.getItem(skillCodeKey) === code) sessionStorage.removeItem(skillCodeKey);
          setAdded(path);
        }}
      />
    </>
  );
}
