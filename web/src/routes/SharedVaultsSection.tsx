// SPDX-License-Identifier: AGPL-3.0-or-later
import { errorText } from "../api/error-text";
import { useVaultEvents } from "../api/sharing";
import { FormError } from "../components/form";
import { timeText } from "../components/Sheet";

/** The caller's own events as a member of vaults shared with her, also of vaults she has lost (the section's body, within Settings). */
export default function SharedVaultsSection() {
  const events = useVaultEvents();
  return (
    <>
      <p className="text-text-body">What happened to your access to vaults shared with you: changes of your role, your removal, your leaving. Newest first.</p>
      {events.isError && <FormError>{errorText(events.error)}</FormError>}
      {events.data?.length === 0 && <p className="text-muted">No sharing events yet.</p>}
      {events.data && events.data.length > 0 && (
        <ul className="flex flex-col gap-2">
          {events.data.map((event, i) => (
            <li key={`${i}-${event.at}`} data-event-kind={event.kind} className="flex flex-col">
              <span className="break-words text-text">{event.text}</span>
              <span className="text-sm text-muted">
                {timeText(event.at)}
                {/* The server sends no vault once she has none: it was removed from her or she left it. */}
                {!event.vault && " · No longer shared with you"}
              </span>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
