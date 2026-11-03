// The plugin's version, read from its manifest: the version lives only in the manifests (the Codex one here and
// .claude-plugin/plugin.json for Claude Code; a test checks they agree).

import { readFileSync } from "node:fs";

export const VERSION = JSON.parse(readFileSync(new URL("../../plugin.json", import.meta.url), "utf8")).version;
