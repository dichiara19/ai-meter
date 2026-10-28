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
| `id` | Lowercase letters, digits and `-`. `claude`, `openai`, `copilot` and `local` are reserved. |
| `name`, `description` | Shown in Settings and in the panel. Write them in English and translate them in `i18n`. |
| `monogram` | Letter shown when there is no logo (default: first letter of `name`). |
| `color` | Brand color for light and dark theme, `#RRGGBB`. |
| `detect` | When the service counts as installed, which turns it on at first run: `paths` (files or folders), `env` (environment variables), `commands` (programs on the `PATH`). |
| `logo` | `file` (PNG) or `exe` (its icon) of the installed app, first one found wins; paths accept `%VARIABLES%` and `~`. Without an installed app, `image` (and `imageDark` for the dark theme) is a PNG next to the definition file, for example the service's official logo. |
| `processes` | `[{ "name": "app.exe", "title": "App open" }]`: shown as an open app while the process runs. |
| `auth` | Where the secret comes from, see below. |
| `spend` | For services billed in credits: paths of `total` (spent to date), `balance`, and optionally `day`, `week`, `month` (spent in the current UTC period), with `currency` or `currencyPath`. Provider-reported period totals support budget bars, forecasts and threshold alerts. Totals and balances alone produce estimates labelled with their observation start; they cannot establish remaining budget or trigger threshold alerts. A first reading is unavailable, not zero. History is isolated by a hash of the credential and currency; legacy histories without that identity do not seed a baseline. Today's agent models are also shown. |
| `request` | `url`, `method` (default `GET`), `headers`, `body`, `timeout` in seconds (15), `interval` in minutes between requests (5; three times longer on battery). |
| `plan` | Path of the plan name in the answer, shown as a badge. |
| `limits` | The bars, see below. |
| `notes` | Extra lines under the bars, see below. |
| `i18n` | Translations of your texts: `{ "it": { "English text": "Testo italiano" } }`. Languages: `it`, `es`, `fr`, `de`, `pt`. |

### `auth`

| `type` | Secret |
|---|---|
| `none` | No secret. |
| `key` | An API key the user pastes in Settings (encrypted with Windows data protection). `page` is the address where keys are created. |
| `env` | An environment variable. |
| `file` | A file. With `field` the file is JSON and the token is that field; `expires` is the path of its expiry. |
| `sqlite` | A value read with `query` from the SQLite database at `path` (read only), for apps that keep their sign-in there. |

All types also accept `env`: variables tried first. With `type: "key"`, `ids` lists the names coding agents use for the service (OpenCode, Pi, Crush; default: the definition's `id`): if no key is saved in AI Meter, it uses the one those programs keep, or an `export` of an `env` variable in the shell profile, on Windows and in WSL while it is running. `jwt: true` checks the token's `exp` claim and treats an expired token as missing. `notice` replaces the message shown when the secret is missing.

AI Meter never refreshes another app's sign-in: if a token has expired, the user opens that app and it renews it.

In `url`, `headers` and `body` you can use `{token}`, `{jwt.claim}` (a claim of the token), `{env:NAME}`, with the filters `|urlencode` and `|afterLast:X` (the text after the last character `X`).

### Paths

A path picks a value from the JSON answer: `data.usage`, `items[0].name`, `items[-1]` (last), `limits[type=session].used` (first element whose `type` is `session`), `$` (the whole answer).

### `limits`

| Field | Meaning |
|---|---|
| `label` | English text, translatable in `i18n`. |
| `short` | Label for the compact pill (for example `5h`, `7d`). |
| `percent` | Path of the used percentage; `percentScale: 100` if the API returns a fraction. |
| `used`, `limit`, `remaining` | Paths of amounts: the percentage is computed from two of them. |
| `currency`, `currencyPath`, `scale` | With a currency the row shows money left (`scale: 0.01` for cents). |
| `balance` | Path of a balance with no limit: only the amount is shown, without a bar. |
| `unlimited` | Path that, when true, shows *unlimited*. |
| `starts`, `resets` | Paths of the start and reset time: ISO text, Unix seconds or milliseconds. With both, AI Meter knows the length of the window: it uses it for the forecast, the reset notification and the tip before a reset (`short` values `5h`, `7g` and `mese` also tell it the length). |
| `when`, `unless` | Paths (one or a list): the row is shown only if all `when` are true and no `unless` is. |

### `notes`

`{ "text": "On-demand usage: {0} of {1}", "values": [{ "path": "...", "currency": "USD", "scale": 0.01 }], "when": "..." }`: one line per note. A value without a currency is a number (`decimals` sets the decimals).

## Checklist for a pull request

- The endpoint is documented by the service, or used by its official app, and returns only the user's own usage.
- The definition works with the raw answer saved in `%APPDATA%\AIMeter\<id>-usage-raw.json`.
- No secrets in the file; the service is added to the network list in the README's privacy policy.
