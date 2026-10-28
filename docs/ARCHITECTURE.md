# Local history architecture

## Responsibilities

The standalone application keeps its C# 5 / .NET Framework 4.8 build, existing dependencies and native WPF surfaces. The history feature has explicit boundaries:

| Component | Responsibility | Lifetime / dependencies |
| --- | --- | --- |
| `Controller` in `App.cs` | Compose the services; schedule collection; handle lock/sleep, settings, notifications and windows. | Application host. Creates no history worker or UI polling loop. |
| `HistoryCollector` | Read changed local sources and emit whitelisted numeric events. | Uses existing read-only adapters and the already-running WSL check. Source cursors belong to the archive transaction. |
| `HistoryStore` | Own the schema, transactions, scopes, deduplication, feedback, export and deletion. | Receives an explicit archive path. Opens and disposes connections per operation; serialises operations through one gate. |
| `HistoryDatabase` | Bind parameters, execute commands, stream rows and release SQLite handles. | Windows SQLite ABI only; all user/source values use bindings. The repository owns SQL and schema choices. |
| `HistoryAnalysis` | Build coherent read-only projections and numeric anomaly signals. | No windows or controls. The archive retains facts; projections never rewrite them. |
| `HistoryWindow` | Translate and present projections; accept explicit rating/export/deletion actions. | UI thread for controls; database work runs on tasks. Uses the existing Fluent palette and standard keyboard-operable WPF controls. |
| `HistoryVisuals` | Reusable settings-style cards, messages, chart axes and accessible disclosure controls. | Presentation only; uses the shared palette and system colours in high contrast. |
| `StringsHistory` | Feature translations. | Five translations plus English source keys, registered through the existing localisation engine. |

There is one writer and one storage implementation. An interface hierarchy or dependency-injection container would add no current capability. If a future host requires a second implementation, extract that contract at the point of need. Existing provider collection and forecasts remain independently usable.

Version 1 uses SQLite `user_version`, transactions, WAL, foreign keys and indexes for metric/time/session queries. A newer, unsupported schema is rejected without deleting or recreating the archive. Provider/account/metric/reset boundaries stay explicit; costs, token counters, quotas and feedback are different facts with different units. No translated caption is parsed to reconstruct a number.

Persistent settings and history windows share the same native chrome helper, including the extended glass frame required by the backdrop. Content uses a separate layer and padded cards rather than the legacy tab-control frame. Charts use recorded points only; single samples, missing values and reported zeroes have distinct explanations. Comparison readiness is calculated alongside the actual anomaly baseline, so a new or unidentified account cannot be presented as having passed an anomaly check.

Daily projections resolve local day boundaries through .NET rather than SQLite's optional local-time conversion. Indexed UTC buckets identify candidate dates, and range aggregates retain the original event counters without loading transcripts. Day boundaries account for daylight-saving transitions, including skipped or repeated midnights.
