// SPDX-License-Identifier: AGPL-3.0-or-later
import { Component, useEffect, type ReactNode } from "react";
import { useRouteError } from "react-router-dom";
import { AuthScreen, primaryButtonClass, secondaryButtonClass } from "./form";

/** Indirect, so tests can watch it: jsdom's `location.reload` can't be replaced. */
export const page = { reload: () => window.location.reload() };

/**
 * Set for the one automatic reload after an update, so a chunk that is still missing afterwards shows the page
 * instead of reloading forever. A flag only, nothing of the app's content. Cleared by the next navigation that works.
 */
export const RELOADED = "kumbu.reloaded";

export function forgetReload() {
  try {
    sessionStorage.removeItem(RELOADED);
  } catch {
    // Storage blocked: there is no flag to forget.
  }
}

/** The browsers' words for a lazy chunk that isn't there (Chrome, Firefox, Safari): after a redeploy, typically. */
const chunkMissing = /Failed to fetch dynamically imported module|error loading dynamically imported module|Importing a module script failed/i;

function isChunkMissing(error: unknown) {
  return error instanceof Error && chunkMissing.test(error.message);
}

/** Answers whether to reload on its own now, and notes that it did; without storage, never (no loop). */
function reloadOnce() {
  try {
    if (sessionStorage.getItem(RELOADED)) return false;
    sessionStorage.setItem(RELOADED, "1");
    return true;
  } catch {
    return false;
  }
}

/** The route error page: a part of the app that failed to load (an old chunk after an update) or to render. */
export function RouteError() {
  const error = useRouteError();

  useEffect(() => {
    if (isChunkMissing(error) && reloadOnce()) page.reload();
  }, [error]);

  return (
    <AuthScreen title="This page didn't load" lead="Kumbu² may have been updated meanwhile. Reloading usually helps.">
      <button type="button" onClick={page.reload} className={primaryButtonClass}>
        Reload
      </button>
    </AuthScreen>
  );
}

/** For a part loaded with React.lazy inside a page: its failure shows `fallback` in its place, not a blank. */
export class LoadBoundary extends Component<{ fallback: ReactNode; children: ReactNode }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  render() {
    return this.state.failed ? this.props.fallback : this.props.children;
  }
}

/** A sheet that failed to load, with the text open in the editor underneath kept: reloading is the user's call. */
export function LoadFailedSheet({ onClose }: { onClose(): void }) {
  return (
    <div className="absolute inset-0 z-30 flex items-end justify-center bg-black/55 p-3 sm:items-center">
      <div role="alertdialog" aria-labelledby="load-failed-title" className="k-sheet flex w-full max-w-md flex-col gap-3 rounded-[16px] border border-line bg-surface p-[18px]">
        <h2 id="load-failed-title" className="text-[20px] font-bold text-text">
          This didn't load
        </h2>
        <p className="text-text-body">Kumbu² may have been updated meanwhile. Reloading usually helps.</p>
        <div className="mt-1 flex gap-2.5">
          <button type="button" onClick={onClose} className={`${secondaryButtonClass} flex-1`}>
            Close
          </button>
          <button type="button" onClick={page.reload} className={`${primaryButtonClass} flex-1`}>
            Reload
          </button>
        </div>
      </div>
    </div>
  );
}
