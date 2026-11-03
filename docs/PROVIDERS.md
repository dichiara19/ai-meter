# Adding a service

Most services in AI Meter are described by a small JSON file instead of code. A definition says where to find the sign-in or API key, which address to ask, and how to read the answer. To add a service you write one file: no C#, no build.

- Built-in definitions live in [`providers/`](../providers). A pull request with a new file is all it takes to add a service for everyone.
- Your own definitions go in `%APPDATA%\AIMeter\providers` (Settings → *Add a service* → *Open folder*). Restart AI Meter to load them. A file with the same `id` as a built-in one replaces it, which is handy to test changes.
- After each successful request AI Meter saves the raw answer to `%APPDATA%\AIMeter\<id>-usage-raw.json` (without your secrets), so you can see which fields are available.

Claude, ChatGPT/Codex and GitHub Copilot need custom sign-in flows and stay in code; local models have their own module.

> A definition can read local files and send what it reads to the address it names. Only install definitions you trust, as you would a script.

## Example

```json
{
  "id": "openrouter",
  "name": "OpenRouter",
  "description": "Spending and limit of your OpenRouter API key.",
  "color": { "light": "#5A5FD6", "dark": "#A6A9F7" },
  "detect": { "env": ["OPENROUTER_API_KEY"] },
  "auth": { "type": "key", "env": ["OPENROUTER_API_KEY"], "page": "https://openrouter.ai/settings/keys" },
  "request": { "url": "https://openrouter.ai/api/v1/key", "headers": { "Authorization": "Bearer {token}" }, "interval": 5 },
  "limits": [
    { "label": "Key limit", "short": "$", "when": "data.limit", "used": "data.usage", "limit": "data.limit", "currency": "USD" },
    { "label": "Spent with this key", "unless": "data.limit", "balance": "data.usage", "currency": "USD" }
  ],
  "i18n": { "it": { "Key limit": "Limite della chiave" } }
}
```

## Fields

| Field | Meaning |
|---|---|
| `id` | Lowercase letters, digits and `-`. `claude`, `openai`, `copilot`, `antigravity` and `local` are reserved. |
| `name`, `description` | Shown in Settings and in the panel. Write them in English and translate them in `i18n`. |
| `monogram` | Letter shown when there is no logo (default: first letter of `name`). |
| `color` | Brand color for light and dark theme, `#RRGGBB`. |
| `detect` | When the service counts as installed, which turns it on at first run: `paths` (files or folders), `env` (environment variables), `commands` (programs on the `PATH`). |
| `logo` | `file` (PNG) or `exe` (its icon) of the installed app, first one found wins; paths accept `%VARIABLES%` and `~`. Without an installed app, `image` (and `imageDark` for the dark theme) is a PNG next to the definition file, for example the service's official logo. |
| `processes` | `[{ "name": "app.exe", "title": "App open" }]`: shown as an open app while the process runs. |
| `auth` | Where the secret comes from, see below. |
| `spend` | For services billed in credits: paths of `total` (spent to date), `balance`, and optionally `day`, `week`, `month` (spent in the current UTC period), with `currency` or `currencyPath`, and `scale` to turn the values into money. A value can also be `{ "items", "value", "above" }`: over the array at `items`, the sum of what each item's `value` exceeds `above` by, such as Cloudflare's paid Neurons beyond the free 10,000 of each day. Provider-reported period totals support budget bars, forecasts and threshold alerts. Totals and balances alone produce estimates labelled with their observation start; they cannot establish remaining budget or trigger threshold alerts. A first reading is unavailable, not zero. History is isolated by a hash of the credential and currency; legacy histories without that identity do not seed a baseline. Today's agent models are also shown, with the cost OpenCode, Pi, Kilo and Hermes computed. |
| `request` | `url`, `method` (default `GET`), `headers`, `body`, `timeout` in seconds (15), `interval` in minutes between requests (5; three times longer on battery). For APIs that report failures in a successful answer, as GraphQL does, `error` is the path of the error and `errorNotice` the message shown when it is present. |
| `keys` | Extra values a request needs besides the main secret, by slot name, such as an account id: `{ "account": { "label", "description", "notice", "env", "beside", "secret", "pattern", "page", "pageLabel" } }`. Requests use them as `{key:account}`. `"secret": false` marks a value that is not a secret, such as an account ID: it is typed in plain sight. `pattern` is a regular expression, matched ignoring case: wherever a value comes from, only the part that matches it is used, so an ID pasted with the rest of the address it was copied from still works, and Settings does not save a value that does not match; a copied text that matches fills the empty box when you come back to it. `page` is where to find the value, opened by a link labelled `pageLabel`. A value comes from the one saved in Settings, then the first `env` variable set, then the field `beside` of the entry where a coding agent keeps the key (for OpenCode's `auth.json`, `metadata.accountId`), or one of the `env` variables exported in the same home. While none is found the request is not sent: the card shows `notice` and Settings offers the slot, with `label` and `description`. A slot used only by a `more` request is offered after the service refuses the main key. |
| `plan` | Path of the plan name in the answer, shown as a badge. |
| `limits` | The bars, see below. |
| `notes` | Extra lines under the bars, see below. |
| `freeModels` | A request (`url`, `headers`, `interval` in minutes, default 360; `{token}` is filled as in `request`) for the service's model list, see below. With a `request`, the list is read only while that request succeeds. Without one, it is read whenever the key it names is available, so a service without a usage endpoint can still list its models. Such a service is a **model catalogue**: it has nothing to show on the taskbar, in the panel or on the widget, so it appears only in Settings, under *Model catalogues for AI apps*. It stays off until the user turns it on, even when its key is found, and its models reach AI apps through MCP. |
| `i18n` | Translations of your texts: `{ "it": { "English text": "Testo italiano" } }`. Languages: `it`, `es`, `fr`, `de`, `pt`. |

### `auth`

| `type` | Secret |
|---|---|
| `none` | No secret. |
| `key` | An API key the user pastes in Settings (encrypted with Windows data protection). `page` is the address where keys are created. |
| `env` | An environment variable. |
| `file` | A file. With `field` the file is JSON and the token is that field; `expires` is the path of its expiry. |
| `sqlite` | A value read with `query` from the SQLite database at `path` (read only), for apps that keep their sign-in there. |

All types also accept `env`: variables tried first. With `type: "key"`, `ids` lists the names coding agents use for the service (OpenCode, Pi, Crush, or any agent whose definition in `agents/*.json` gives `keys`; default: the definition's `id`): if no key is saved in AI Meter, it uses the one those programs keep, or an `export` of an `env` variable in the shell profile, on Windows and in WSL while it is running. `jwt: true` checks the token's `exp` claim and treats an expired token as missing. `notice` replaces the message shown when the secret is missing.

AI Meter never refreshes another app's sign-in: if a token has expired, the user opens that app and it renews it.

In `url`, `headers` and `body` you can use `{token}`, `{jwt.claim}` (a claim of the token), `{env:NAME}`, `{key:slot}` (a value of `keys`), `{date:utc}` (today's UTC date as `yyyy-MM-dd`), `{date:utc-week}` and `{date:utc-month}` (the Monday of this UTC week and the first of this UTC month), with the filters `|urlencode` and `|afterLast:X` (the text after the last character `X`).

### `freeModels`

| Field | Meaning |
|---|---|
| `format` | `openrouter` (default): `data[].pricing`, `expiration_date`, `context_length`, `top_provider.max_completion_tokens`. Only text models with an explicit zero prompt and completion price are kept; an empty `pricing` means unknown, not free. `openai`: `data[].id` only, as NVIDIA's list. `gemini`: `models[]` with `inputTokenLimit`, `outputTokenLimit` and `supportedGenerationMethods`. In the last two, embedding, ranking, safety, speech and image models are left out by name. |
| `free` | For lists without prices: `status` (`unknown` unless the service documents it), `kind`, `reference`, `checked` (date) and a `note` for agents saying why. |
| `pool` | Name of the counter the limits draw from, such as `openrouter:free`. `{model}` stands for each model id when every model has its own. |
| `limits` | Rate limits of the free models. Each one has a `metric` (`requests_per_minute`, `requests_per_day`, `tokens_per_minute`, `input_tokens_per_minute`, `tokens_per_day`, `concurrent_requests`), a `scope` (`account`, `project`, `model`), `perModel`, and a `source` (`documented`, `provider_reported` or `unknown`) with `reference`, `checked` and `note`. `limit`, `used` and `remaining` are numbers stated in the file, or paths into the usage answer for `provider_reported`. `period: "day"` with `resetZone` (`UTC` by default, or `America/Los_Angeles`) gives the next reset. A limit that exists but has no known value keeps its metric with `source: "unknown"`: never write a guess. |

### Paths

A path picks a value from the JSON answer: `data.usage`, `items[0].name`, `items[-1]` (last), `limits[type=session].used` (first element whose `type` is `session`), `$` (the whole answer).

### `limits`

| Field | Meaning |
|---|---|
| `label` | English text, translatable in `i18n`. |
| `short` | Window kind: `5h`, `7d`, `month` or `1d` for the main window of that length, `m` for a secondary limit, `$` for money. The older codes `7g`, `mese` and `1g` are still accepted. |
| `percent` | Path of the used percentage; `percentScale: 100` if the API returns a fraction. |
| `used`, `limit`, `remaining` | Paths of amounts, or numbers written in the file for a documented limit (`"limit": 10000`): the percentage is computed from two of them. |
| `emptyIsZero` | Path of the list that holds `used`, for services that answer with no rows when nothing was used: only a list that is present and empty counts as zero. Otherwise a missing value is unknown, never zero. |
| `currency`, `currencyPath`, `scale` | With a currency the row shows money left (`scale: 0.01` for cents). |
| `balance` | Path of a balance with no limit: only the amount is shown, without a bar. |
| `unlimited` | Path that, when true, shows *unlimited*. |
| `starts`, `resets` | Paths of the start and reset time: ISO text, Unix seconds or milliseconds. With both, AI Meter knows the length of the window: it uses it for the forecast, the reset notification and the tip before a reset (`short` values `5h`, `7d`, `month` and `1d` also tell it the length). |
| `period` | `day`, `week` (from Monday) or `month` in UTC: the window's start and reset, for counters whose answer has no reset field. |
| `when`, `unless` | Paths (one or a list): the row is shown only if all `when` are true and no `unless` is. |

### `notes`

`{ "text": "On-demand usage: {0} of {1}", "values": [{ "path": "...", "currency": "USD", "scale": 0.01 }], "when": "..." }`: one line per note. A value without a currency is a number (`decimals` sets the decimals).

## Checklist for a pull request

- The endpoint is documented by the service, or used by its official app, and returns only the user's own usage.
- The definition works with the raw answer saved in `%APPDATA%\AIMeter\<id>-usage-raw.json`.
- No secrets in the file; the service is added to the network list in the README's privacy policy.
