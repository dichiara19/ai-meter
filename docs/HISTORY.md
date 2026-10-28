# Local usage history

AI Meter keeps a local archive from the first collection after this version starts. Open **Usage history** from the notification-area menu or **Settings → Usage history → Open dashboard**. Collection is enabled by default, can be disabled without deleting existing records, and continues when all surfaces are hidden. Existing provider refresh intervals and power-saving behaviour still apply.

## What is retained

The archive is `%APPDATA%\AIMeter\history.sqlite3`. Windows supplies SQLite through `winsqlite3.dll`; no package or external service is installed. Indexed tables retain numeric observations and response records without a time-based purge. Daily and model summaries are calculated from the indexed records when requested, so the original detail remains available. The dashboard displays archive size, supports a consistent JSONL export with UTC Unix-millisecond timestamps, and can delete all retained history and ratings. Collection starts afresh after deletion if enabled.

| Data | Source and meaning |
| --- | --- |
| Quota readings | The original measurement time, capture time, limit identity, reset boundaries, percentage, monetary allowance, plan and source. Repeated snapshots of the same response add no duplicate observation. Cached responses and stale extras add no fresh readings. |
| Spending and balances | Numeric amounts, weekly usage breakdowns and units retained separately from translated display captions. Estimated spending stays explicitly estimated. Independently fetched extras retain their own measurement time. |
| Token records | Claude Code assistant usage, Codex token-count events, Pi assistant usage, and OpenCode/Kilo message usage. Input, output, cache reads and cache writes remain separate nullable counters. Codex input excludes its separately reported cached input. |
| Computed costs | Costs reported by Pi and OpenCode/Kilo in USD. These are tool-computed API estimates, including the subset of responses for which a cost is available. They are not a subscription invoice. |
| Session feedback | An optional, editable value: yes, partly, no, or unrated. It annotates observed session metrics without changing them. |
| Coverage | Interruptions, inaccessible sources, rewritten logs and bounded-read limitations. Missing measurements remain missing. |

The archive does **not** retain prompts, response text, source code, session titles, paths, raw session/message identifiers, email addresses or authentication tokens. Source files are opened read-only; a document exists in memory only whilst its line is parsed. Hashes identify sessions and responses locally. Export contains the retained metrics, metadata and feedback, not source documents or file cursors. Existing provider caches and application logs remain separate from this archive.

Claude account and organisation identifiers, when available in its local profile, scope quota readings across credential renewal. Otherwise the credential fingerprint is used. Codex supplies an account ID or email through `account/read` with `refreshToken=false`; only a hash is retained. The current Codex response may not identify a workspace separately. Definition-based providers retain their existing credential scopes. When account identity is unavailable, readings are marked unidentified and anomaly comparisons are disabled; their cycle rows cannot prove account continuity. A different credential fingerprint can represent the same account, so do not sum such rows as separate subscriptions.

## Collection boundaries

Windows lock and sleep states pause collection independently: resuming power does not override a locked session. Explicit pauses are recorded on resumption. A heartbeat gap over five minutes also records downtime after a crash or restart. The archive cannot recover provider-side activity without observable local records. Individual Claude/ChatGPT web chats, unsupported tools and provider-internal retries may therefore have quota readings without token records. Local model presence and context size are not treated as billed token consumption.

The history reader scans at most once per minute and never starts WSL. Hidden surfaces keep the normal, slower worker cadence. Each changed JSONL file advances incrementally, including across app restarts. Metrics and source offsets commit together in one transaction; a failed batch can be replayed. Partial lines wait for completion, streaming updates replace counters, and inherited Pi replies keep their first observed owner across forks. Independent replies retain distinct identities even when their content matches.

To bound memory and I/O, a JSONL batch reads at most 2 MiB per file, approximately 8 MiB across files, and 200 pending files per scan. Lines over 1 MiB are skipped with a coverage marker. The first encounter with an existing file over 2 MiB reads its tail and records that limitation; earlier lines are not claimed as recovered. Codex discovery checks recent day folders on each scan and reindexes older folders hourly, so reopening a very old session can delay its first capture. OpenCode/Kilo use the latest 5,000 messages created since collection began and revisit them when their database changes; a cap records a source limitation. Large WSL databases that cannot be safely copied by the existing read-only adapter remain unavailable. These markers describe coverage, not zero consumption.

## Reading the dashboard

- **Overview:** model/tool token summaries, computed-cost coverage, feedback coverage, quota-rate signals and collection limitations.
- **Sessions:** up to 200 recent sessions, identified by time, tool, model and an opaque reference. Token/cost totals include observed responses within the selected period. An account's quota is not apportioned among concurrent sessions.
- **Quota cycles:** separate rows for each account, limit, reset cycle, plan and allowance. Peak usage and remaining quota at the last reading are observed values; the latter does not prove how much expired unused after collection stopped. Overlapping five-hour and weekly limits must not be added together. Spending and balance rows preserve amounts, units and quality.
- **Daily activity:** local-calendar-day totals for the selected period. Unreported counters and costs are excluded from totals; a dash means unavailable, not zero. Export preserves each record's null fields for closer investigation.

The overview leads with summary counts and an activity chart. The chart selector keeps responses, token categories and computed costs in separate units. Quota charts show their original reading times with labelled percentage axes; daily charts show recorded days only. Detailed tables and collection information expand on request. Session selection and feedback appear side by side when space permits and stack in narrower windows.

Empty states distinguish a new archive from a filter with no results, a first measurement, unavailable counters and explicitly reported zeroes. Until eight comparable active intervals exist, the dashboard explains that the baseline is still forming; it does not claim that no anomalies were found. Readings without account identity do not promise a baseline that cannot be established. History windows share the settings chrome, palette and spacing, have visible keyboard focus, and use system colours in high contrast.

Analysis is limited to the latest 5,000 quota readings, 200 cycle rows and 100 model/tool groups for the selected period. A chart draws at most 300 points, with breaks at reset changes and gaps over 20 minutes. All stored detail remains in the export. The dashboard refreshes on request rather than running another background polling loop.

Quota-rate signals require eight positive comparison intervals of 5–20 minutes for the same account, metric, plan, allowance, currency and source. Idle intervals cannot establish an active-consumption baseline. Decreases, reset transitions and gaps are excluded. A candidate interval must consume at least three percentage points and exceed both three times the median rate and a robust median-deviation threshold. Three consecutive candidates indicate a persistent increase. These are exploratory signals: task complexity, model routing and other sessions are not controlled, so a signal cannot prove that a provider changed its billing rules. Signals appear in the dashboard; existing low-quota notifications remain separate.

## Optional feedback

**Settings → Usage history → Ask for session feedback** is off by default. Once enabled, a session with a recorded response may be suggested after 30 minutes of inactivity, provided no observed session is busy. Only sessions active after opting in and within the last two hours are eligible. Requests are durably limited to one per hour and once per session, including after restarting AI Meter. Windows notification preferences apply. Clicking the notification opens that session in the dashboard, where one answer is sufficient:

> Was the result worth this session's consumption?
>
> Rate the useful work obtained relative to the quota or money consumed.
>
> 👍 Yes · 😐 Partly · 👎 No

Inactivity is a heuristic, not an assertion that the tool has terminated. The rating measures perceived useful output for consumption, rather than general satisfaction with a provider, model or harness. It can also be entered manually, changed or cleared. Ignoring the request never assigns a negative rating. Feedback statistics display rated/observed session coverage and exclude unrated sessions from the rating distribution.

## Verification

Run `powershell -NoProfile -Sta -ExecutionPolicy Bypass -File tests/run.ps1`. Tests use temporary storage and loopback HTTP fixtures; no saved accounts or running AI Meter instance are used. History regressions cover transactions, replay, UTF-8 boundaries, large/invalid input, fork and streaming deduplication, unknown values, scoped cycles, independent response times, feedback persistence, deletion, export, pause states and anomaly baselines.

Run `powershell -NoProfile -Sta -ExecutionPolicy Bypass -File tools/render-history.ps1` to render the actual WPF controls with synthetic history. It covers both themes, narrow layouts, empty/filtered history, one reading, reported zeroes and system-colour overrides. A hidden native window also checks that the shared settings chrome initialises without starting or replacing AI Meter. Add `-Language it` for Italian previews. Output defaults to `bin/history-preview`. Narrator, actual high-contrast switching, real notifications, multi-monitor DPI and packaged suspend/resume still require interactive Windows validation.
