// The shell a described dispatch runs its commands through (ADR 0004 decision 5). AI Meter's managed Pi comes with
// arguments that give it the PowerShell tool and no bash (or no shell at all for read_only), and, for modify, a
// `git_bash` alternative that gives it the bash tool instead. With the bash tool, Pi looks for Git Bash in the usual
// folders and then for any bash.exe on PATH, where it can find WSL's launcher in System32 and silently run commands
// inside WSL. So the plugin uses the alternative only when it has found Git for Windows itself, and then puts Git's bin
// folder first on PATH; otherwise the agent keeps the PowerShell tool. WSL's bash.exe is never chosen.
//
// Pure functions of an environment and a file-existence check, so they can be tested on any system. Paths are
// Windows paths whatever system runs the tests, hence node:path's win32 flavour.

import { existsSync } from "node:fs";
import { win32 } from "node:path";

/** The value of an environment variable, its name compared without case, as Windows does (Path and PATH). */
const valueOf = (env, name) => env[keyOf(env, name) ?? name];

/** The spelling an environment uses for a variable's name, or null when it has none. */
const keyOf = (env, name) => Object.keys(env).find((key) => key.toLowerCase() === name.toLowerCase()) ?? null;

/** Whether path is folder or inside it, compared without case. */
function within(path, folder) {
  const a = win32.normalize(path).toLowerCase().replace(/\\+$/, "");
  const b = win32.normalize(folder).toLowerCase().replace(/\\+$/, "");
  return a === b || a.startsWith(b + "\\");
}

/**
 * The bin folder of Git for Windows (the one that holds bash.exe), or null. The standard installation folders come
 * first, then, for each PATH entry that holds git.exe, its sibling bin folder (Git puts ...\Git\cmd on PATH). A folder
 * under the Windows folder is never returned: WSL's bash.exe launcher lives there.
 */
export function findGitBash(env = process.env, exists = existsSync) {
  const places = [];
  for (const name of ["ProgramFiles", "ProgramW6432", "ProgramFiles(x86)"]) {
    const root = valueOf(env, name);
    if (root) places.push(win32.join(root, "Git", "bin"));
  }
  const local = valueOf(env, "LOCALAPPDATA");
  if (local) places.push(win32.join(local, "Programs", "Git", "bin"));
  for (const entry of String(valueOf(env, "PATH") || "").split(";")) {
    const folder = entry.trim().replace(/^"(.*)"$/, "$1");
    if (folder && exists(win32.join(folder, "git.exe"))) places.push(win32.resolve(folder, "..", "bin"));
  }

  const windows = valueOf(env, "SystemRoot") || valueOf(env, "windir") || "C:\\Windows";
  return places.find((folder) => !within(folder, windows) && exists(win32.join(folder, "bash.exe"))) ?? null;
}

/**
 * How to start a described dispatch: { name, arguments, environment }. name is the shell its commands run through
 * ("git_bash", "powershell", "none" for no commands, or null when AI Meter does not say), arguments replace
 * plan.arguments, and environment is merged over plan.environment. env is the environment the agent would start with.
 * A dispatch that is not the managed copy is returned as described.
 */
export function shellFor(plan, env = process.env, exists = existsSync) {
  const described = { name: plan.commands ?? null, arguments: plan.arguments, environment: {} };
  if (!plan.managed) return described;
  const alternative = (plan.alternatives || []).find((a) => a.shell === "git_bash");
  const bin = alternative ? findGitBash(env, exists) : null;
  if (!bin) return described;

  // The PATH key the environment already uses, so that the agent does not get two PATH variables of different case.
  const key = keyOf(env, "PATH") ?? "PATH";
  const path = env[key] ? bin + ";" + env[key] : bin;
  return { name: "git_bash", arguments: alternative.arguments, environment: { [key]: path } };
}
