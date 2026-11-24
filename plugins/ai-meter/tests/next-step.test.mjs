// The next step a coordinator relays to the user when no candidate could run (nextStep in scripts/lib/report.mjs,
// ADR 0005 decision 5), from AI Meter's runtimes made up here.

import assert from "node:assert/strict";
import { test } from "node:test";
import { nextStep } from "../scripts/lib/report.mjs";

const SETTINGS = "AI Meter › Settings › AI apps › Delegated tasks";
const copy = (where, fields = {}) => ({ name: "pi", where, ...(where === "wsl" ? { distro: "Ubuntu" } : {}), installed: false, reachable: true, version: "1.5.2", services: [], set_up_in: SETTINGS, ...fields });
const own = { name: "opencode", where: "windows" };

test("no agent and no managed copy: AI Meter can set one up, inside WSL where there is WSL, else on Windows", () => {
  for (const wsl of ["running", "stopped"]) {
    assert.equal(nextStep({ found: [], managed: [copy("windows"), copy("wsl")], environment: { wsl, distro: "Ubuntu", git_for_windows: true } }),
      `No coding agent is set up on this PC for delegated tasks: the user can have AI Meter set up its own copy of Pi inside WSL (Ubuntu), in ${SETTINGS}.`, wsl);
  }
  assert.equal(nextStep({ found: [], managed: [copy("windows")], environment: { wsl: "none", git_for_windows: false } }),
    `No coding agent is set up on this PC for delegated tasks: the user can have AI Meter set up its own copy of Pi on Windows, in ${SETTINGS}.`);
});

test("a managed copy without a key: tick a service's key", () => {
  const runtimes = { found: [{ name: "pi", where: "wsl", distro: "Ubuntu", managed: true }], managed: [copy("windows"), copy("wsl", { installed: true })], environment: { wsl: "running" } };
  assert.equal(nextStep(runtimes), `AI Meter's copy of Pi inside WSL (Ubuntu) holds no key yet: the user can tick a service's key for it in ${SETTINGS}.`);
});

test("the copy inside WSL installed but out of reach: start WSL", () => {
  const runtimes = { found: [], managed: [copy("wsl", { installed: true, reachable: false, services: ["openrouter"] })], environment: { wsl: "stopped", distro: "Ubuntu" } };
  assert.equal(nextStep(runtimes), "AI Meter's copy of Pi inside WSL (Ubuntu) cannot be reached: the user can start WSL (Ubuntu), then try again.");
});

test("the first rule that applies wins", () => {
  const keyless = copy("windows", { installed: true });
  const unreachable = copy("wsl", { installed: true, reachable: false, services: ["openrouter"] });
  assert.match(nextStep({ found: [], managed: [unreachable, keyless], environment: { wsl: "stopped" } }), /on Windows holds no key yet/);
});

test("nothing to say: the Windows copy without WSL or Git for Windows, the user's own agents, or no managed copy offered", () => {
  assert.equal(nextStep({ found: [], managed: [copy("windows", { installed: true, services: ["openrouter"] })], environment: { wsl: "none", git_for_windows: false } }), null,
    "PowerShell is used: not a problem");
  assert.equal(nextStep({ found: [own], managed: [copy("windows"), copy("wsl")], environment: { wsl: "running" } }), null, "the user has an agent; excluded says why it has no candidate");
  assert.equal(nextStep({ found: [], unsupported: [] }), null, "an AI Meter that offers no managed copy");
  assert.equal(nextStep(undefined), null);
});
