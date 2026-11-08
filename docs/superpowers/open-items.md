# Open items

Work that waits for the maintainer: pull requests from unattended runs, reviews, hand checks.

| Date | Item | Link |
|---|---|---|
| 2026-11-01 | For 0.3.1, within about a week of 0.3.0: pins a former member with database write access can't forge (design and independent review first; two attempts failed review), and `_loading` not held across retrying transactions | [fixes design §1.2, §1.3](specs/2026-11-01-0.3.0-fixes-design.md) |
| 2026-11-01 | For 0.3.5 or 0.4.0, from the client test of images and PDFs: `--rotate` and the other wishes listed there | [attachments-client-research §6.2](../attachments-client-research.md) |
| 2026-11-01 | Vault sharing (collab) for 0.3.0: draft pull request from the overnighter run; review, plugin 0.2.0 decision, open points in the report | https://github.com/SCAI-NGO/kumbukumbu/pull/125 |
| 2026-11-02 | Fixes for 0.3.0: draft pull request from the overnighter run; review, hand checks named in the report | https://github.com/SCAI-NGO/kumbukumbu/pull/131 |
| 2026-11-02 | Renaming own vaults, and three small things: draft pull request from the overnighter run; review, security review's findings, hand checks named in the report | https://github.com/SCAI-NGO/kumbukumbu/pull/136 |
| 2026-11-02 | A read-only console in the web app: draft pull request from the overnighter run; review, security review's findings, hand test (`img`/`pdf` with real attachments, a phone), open points in the [report](overnighter/2026-11-02-read-only-console-report.md) | https://github.com/SCAI-NGO/kumbukumbu/pull/139 |
| 2026-11-02 | Pins a former member can't forge: Option C chosen. Test the planted user slot, then apply the wording in [the design's §8.1](specs/2026-11-02-pins-design.md). Options A and B follow later, without haste, after an independent review (the design run's attackers were stopped by a safety classifier) | https://github.com/SCAI-NGO/kumbukumbu/pull/137 |
| 2026-11-02 | Attachment wishes for 0.3.5: draft pull request from the overnighter run; review, the verdict on regions with labels, hand checks named in the report | https://github.com/SCAI-NGO/kumbukumbu/pull/140 |
| 2026-11-03 | The polish, for 0.3.2 (0.3.1 went out before it): merged; hand test and open points in the [report](overnighter/2026-11-03-0.3.1-polish-report.md), Task 6's security review (keep; L2 open) | https://github.com/SCAI-NGO/kumbukumbu/pull/148 |
| 2026-11-05 | Publish v1, one note behind a link: draft pull request from the overnighter run; review, the security review's findings, hand test and open points in the [report](overnighter/2026-11-05-publish-report.md) | https://github.com/SCAI-NGO/kumbukumbu/pull/150 |
| 2026-11-07 | Obsidian plugin, from the storage quota: the sync endpoints refuse a write over the owner's limit with `507` and "This vault is out of space." The plugin must treat `507` as final for that file and tell the user, not retry it | [WebDAV design §3.10](specs/2026-11-07-webdav-design.md) |
| 2026-11-07 | WebDAV access, storage quota and access tabs: draft pull request from the overnighter run; review, the security review's findings (two accepted), the lazy hold ending and the realm's encoding (decisions 12 and 8), hand test with the seven clients and open points in the [report](overnighter/2026-11-07-webdav-report.md) | https://github.com/SCAI-NGO/kumbukumbu/pull/152 |
| 2026-11-08 | Obsidian plugin: the versions endpoint leaves text files out unless asked with `text=1` (next to `attachments=1`). Ask for them once the plugin can keep the text files of skill folders (`_skills/…`), or it will not know them as files | [Skills design](specs/2026-11-08-skills-design.md) |
