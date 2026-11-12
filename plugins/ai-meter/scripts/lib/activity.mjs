// What an agent did during a task, from its tool events, so that the coordinator can check the agent's answer
// against evidence: each tool call with a one-line summary of its arguments and whether it failed, the files it wrote
// or edited, and the commands it ran with their exit and the end of their output.
//
// The record is bounded whatever the agent does: only the last CALLS_KEPT calls and COMMANDS_KEPT commands are kept
// (with counts of the earlier ones), FILES_KEPT file paths, and short excerpts. It never holds file contents: a write
// is summarised by its path, and a command's output only by its last characters.
//
// Paths are shortened to the task's own folder: the worktree or snapshot is worktrees/<job id> or snapshots/<job id>,
// so a path through that folder becomes relative to it, and the folder itself becomes ".". The agent sees the folder
// under its own name (a WSL path such as /mnt/c/... for a Windows folder), which is why the end of the path, not the
// whole path, is matched.

const CALLS_KEPT = 30;
const COMMANDS_KEPT = 10;
const FILES_KEPT = 50;
const SUMMARY_CHARACTERS = 160;
const COMMAND_CHARACTERS = 240;
const EXCERPT_CHARACTERS = 300;

// Tool names of Pi and OpenCode, by what they do. Other tools are listed as calls only.
const COMMAND_TOOLS = new Set(["bash", "shell"]);
const WRITING_TOOLS = new Set(["write", "edit", "multiedit", "patch", "apply_patch"]);

export class Activity {
  /** folderName: the name of the task's worktree or snapshot folder (the job id), used to shorten paths. */
  constructor(folderName) {
    this.folder = folderName ? new RegExp(String.raw`(?:[A-Za-z]:)?[^\s"'=:;&|()<>]*[\\/](?:worktrees|snapshots)[\\/]` + escape(folderName) + String.raw`(?:[\\/]|(?=[\s"';&|)]|$))`, "g") : null;
    this.calls = [];
    this.callsLeftOut = 0;
    this.callCount = 0;
    this.failedCount = 0;
    this.pending = new Map();
    this.files = new Set();
    this.filesLeftOut = 0;
    this.commands = [];
    this.commandsLeftOut = 0;
  }

  /** A tool call started. id ties it to its end; a call that never ends is reported as unfinished. */
  begin(id, tool, args) {
    if (id !== undefined && this.pending.has(id)) return;
    const call = { tool: String(tool || "unknown"), args: this.summarise(tool, args), arguments: args };
    this.callCount++;
    this.calls.push(call);
    if (this.calls.length > CALLS_KEPT) {
      this.calls.shift();
      this.callsLeftOut++;
    }
    if (id !== undefined) this.pending.set(id, call);
    return call;
  }

  /**
   * A tool call ended. failed is the agent's own verdict; output is the text it returned (only its end is kept);
   * exitCode, for a command, when the agent reports it.
   */
  end(id, { failed, output, exitCode } = {}) {
    const call = this.pending.get(id);
    if (!call) return;
    this.pending.delete(id);
    call.ended = true;
    const args = call.arguments;
    delete call.arguments;   // A write's arguments hold the file's contents: they are not kept past the call.
    if (failed) {
      call.failed = true;
      this.failedCount++;
    }
    const text = typeof output === "string" ? output : "";
    if (COMMAND_TOOLS.has(call.tool)) {
      const exit = Number.isInteger(exitCode) ? exitCode : exitFrom(text);
      this.command({ command: call.args, ok: !failed, ...(exit === null ? {} : { exit_code: exit }), output_tail: this.excerpt(text, true) });
    } else if (failed && text) {
      call.error = this.excerpt(text, false);
    }
    if (!failed && WRITING_TOOLS.has(call.tool)) for (const path of writtenPaths(call.tool, args)) this.file(this.shorten(path));
  }

  command(entry) {
    this.commands.push(entry);
    if (this.commands.length > COMMANDS_KEPT) {
      this.commands.shift();
      this.commandsLeftOut++;
    }
  }

  file(path) {
    if (!path || this.files.has(path)) return;
    if (this.files.size >= FILES_KEPT) this.filesLeftOut++;
    else this.files.add(path);
  }

  /** The report's activity section, or null when the agent made no tool call. */
  report() {
    if (!this.callCount) return null;
    return {
      tool_calls: this.callCount,
      failed_calls: this.failedCount,
      calls: this.calls.map(({ tool, args, ended, failed, error }) => ({ tool, args, ...(failed ? { failed: true } : {}), ...(ended ? {} : { unfinished: true }), ...(error ? { error } : {}) })),
      ...(this.callsLeftOut ? { earlier_calls_left_out: this.callsLeftOut } : {}),
      files_written: [...this.files],
      ...(this.filesLeftOut ? { more_files_left_out: this.filesLeftOut } : {}),
      commands: this.commands,
      ...(this.commandsLeftOut ? { earlier_commands_left_out: this.commandsLeftOut } : {}),
    };
  }

  /** One line about a call's arguments: the command, the path, the pattern, or else the first short value. */
  summarise(tool, args) {
    if (!args || typeof args !== "object") return "";
    const value = args.command ?? args.cmd ?? args.filePath ?? args.file_path ?? args.path ?? args.pattern ?? args.query ?? args.url ??
      (typeof args.patchText === "string" ? writtenPaths("patch", args).join(", ") : undefined) ??
      Object.values(args).find((v) => typeof v === "string" && v.length < 200);
    return clip(oneLine(this.shorten(typeof value === "string" ? value : JSON.stringify(value ?? ""))), COMMAND_TOOLS.has(String(tool)) ? COMMAND_CHARACTERS : SUMMARY_CHARACTERS);
  }

  shorten(text) {
    if (!this.folder || typeof text !== "string") return text;
    return text.replace(this.folder, (match) => (/[\\/]$/.test(match) ? "" : "."));
  }

  /** A short excerpt of a tool's output: its end for a command (where results and errors are), its start otherwise. */
  excerpt(text, tail) {
    const clean = this.shorten(text.replace(/\r\n/g, "\n")).trim();
    if (clean.length <= EXCERPT_CHARACTERS) return clean;
    return tail ? "…" + clean.slice(-EXCERPT_CHARACTERS) : clean.slice(0, EXCERPT_CHARACTERS) + "…";
  }
}

/** The files a writing tool changed: its path argument, or the files a patch names. */
function writtenPaths(tool, args) {
  if (!args || typeof args !== "object") return [];
  const path = args.filePath ?? args.file_path ?? args.path;
  if (typeof path === "string") return [path];
  if (typeof args.patchText === "string") return [...args.patchText.matchAll(/^\*\*\* (?:Add|Update|Delete) File: (.+)$/gm)].map((m) => m[1].trim());
  return [];
}

/** Pi ends a failed command's output with "Command exited with code N". */
function exitFrom(text) {
  const match = /Command exited with code (\d+)\s*$/.exec(text);
  return match ? Number(match[1]) : null;
}

const oneLine = (text) => text.replace(/\s+/g, " ").trim();
const clip = (text, limit) => (text.length > limit ? text.slice(0, limit - 1) + "…" : text);
const escape = (text) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
