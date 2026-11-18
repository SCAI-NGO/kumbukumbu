# Filters for Users, Invitations and Access

Status: design, agreed with the maintainer on 2026-11-18. Target: 0.6.0. A small change; built on top of the console shortcuts and peek branch (#233), touching the web app only.

## Why

Three lists grow quickly with a server's life: the admin's **Users** and **Invitations**, and a vault's **Access** (its members). Past a dozen entries, finding one person or what still needs doing means scrolling. Each list gets a filter by name and a filter by status.

## Goals

- A text filter on the name, case-insensitive, matching anywhere in it.
- A status filter, one status at a time or all, each status with its count.
- The same filter bar, behaving the same way, in all three lists.

## Non-goals

- No filter by role, date or storage. Cut until someone misses it.
- No server-side filtering or paging: the lists come whole from the API today, and the filter works on what is already loaded.
- No filter on the Departures block in Access: it is a prompt to act, not part of the list.

## Anti-goals

- Entries that vanish without the person noticing. Whenever the filter hides something, the view says so.

## Approaches

- **Filter in the browser, one shared filter bar (chosen).** All three lists are fetched whole, so filtering what is loaded is instant and needs no API change.
- **Query parameters on the API.** Rejected: it would add server code and tests for lists that are not paged, and filtering would wait for the network.
- **Each view its own filter.** Rejected: three ways of doing one thing.

## Decisions

- **What a list shows when it opens: everything** (decided by Chris, because the base view is a blank slate from the user's point of view: agency is greatest when people work their way out of the "chaos" in their own way). Filtering always starts with the person.
- **The filter lasts only as long as the view is open; it is not saved** (decided by Chris, following the UX assessment). A saved filter would turn the blank slate into a view sorted by someone else, the person's earlier self: someone returning after a week to "Open" only would think the used invitations had gone. Setting it again costs a click. Within the open view the filter stays put across what people do there: granting a member, revoking an invitation, deactivating a user, any dialog. No URL parameters either: Access is a dialog with no URL, and one rule ("while it is open") beats two.
- **Counts per status** on each status choice (Chris: "das hat enormen Mehrwert"): "All 42 · Open 3 · Used 30 · Expired 8 · Revoked 1" shows at a glance where something needs doing.
- **When the filter hides entries,** a line above the list says "Showing 3 of 42" with a "Show all" button that clears both filters. When nothing matches: "No invitations match." (or users, members), never an empty space.

## The three lists

| List | Name matches | Statuses (in this order) |
|------|--------------|--------------------------|
| Users (`web/src/routes/admin/Users.tsx`) | display name and username | Active, Deactivated |
| Invitations (`web/src/routes/admin/Invitations.tsx`) | the note (an invitation has no name; one without a note matches only an empty text filter) | Open, Used, Expired, Revoked |
| Access (`web/src/shell/AccessList.tsx`) | the member's label | Invited, To confirm (redeemed, waiting for the owner), Member |

The status labels are those the lists already show, so the filter speaks the list's words. "To confirm" is new: the list has no short word for a redeemed invitation today.

## The filter bar

- A search field ("Filter by name", for Invitations "Filter by note") and a single-choice row of statuses headed by "All", each with its count, as a radio group so it is one tab stop and arrow keys move within it.
- Counts follow the text filter: with "anna" typed, the counts say how many of the matching entries are in each status. A status with a count of 0 stays visible and selectable, so the row doesn't jump.
- Matching: the text trimmed, compared with `toLocaleLowerCase()` on both sides, as a substring.
- Shown whenever the list has entries; hidden on an empty list, which keeps its "No invitations yet." One rule rather than a threshold.
- One component in `web/src/components/`, given the entries, how to get an entry's name text and status, and the statuses with their labels; it returns the visible entries along with the bar. The state lives in the view that uses it, so it resets when the view closes.

## Left to the builder

- The layout of the bar at phone width, in the style of the surrounding forms (`form.tsx`).
- Tests (Vitest) first: matching ignores case, counts follow the text filter, "Showing n of m" and "Show all", the filter survives a refetch of the list, one test per view that the filter is wired in.
