# ADR 0006: Where a local model's memory is, for any engine

- **Status:** proposed. Merging its pull request accepts it.
- **Date:** 2026-11-26
- **Keeps:** [ADR 0001](0001-optional-agent-orchestration.md) decision 4: reading a local model starts nothing inside WSL and never loads a model.

## Context

AI Meter shows a loaded local model with its video memory, context and requests in progress. System memory is read only for a server on Windows whose video memory is unknown. For a server in WSL nothing but video memory is read, and that is the whole of `vmmemWSL`.

A model is often not in video memory alone. Engines keep layers or experts in system memory, map the model file and let the system page it from disk, or spill video memory into shared memory. A reading taken on 2026-11-26 shows the gap: a 12.5 GB model served in WSL behind llama-swap held 11.5 GB of system memory (the experts of a mixture-of-experts model, kept on the host) and about 7 GB of an 8 GB card. AI Meter showed the video memory and no system memory.

Three things were checked on that PC, with read requests only:

- `\\wsl.localhost\<distribution>\proc\<pid>\status` and `cmdline` are readable from Windows as files. `status` gives resident memory split into anonymous (`RssAnon`), mapped from files (`RssFile`) and shared, plus what was swapped out (`VmSwap`).
- The command line of the server process names its port and its model file, so the process behind a port can be found without starting anything in WSL.
- The engine there answers llama-server's `/props` and `/slots`, so AI Meter takes it for llama.cpp, and its own `/stats` (context cache, experts, transfers between device and host) is never read.

What engines say about their own memory differs widely. Ollama reports the size in video memory; llama-server reports none; some report a key-value cache in tokens, none checked reports it in bytes.

## Decision

1. **Memory is measured from the process, whatever the engine.**
   - The base figures come from the operating system, for the server's process and the processes it started. No engine has to cooperate.
   - What an engine reports about itself refines those figures and never replaces the measurement silently: decision 3 says how the two are told apart.
2. **One description of where a model is.** Each loaded model carries, when known:
   - video memory, and on Windows the shared GPU memory it spilt into;
   - system memory, split into private (anonymous) and mapped from the model file;
   - memory swapped out;
   - the size of the model file, from which the part still on disk is derived.
   - "Unknown" is its own state, distinct from zero. A figure that cannot be read is not shown.
3. **Every figure says where it comes from.**
   - *Reported* by the engine, *measured* from the process, or *estimated* by AI Meter.
   - The panel shows an estimate as one. The MCP answer carries the origin beside the value.
4. **Finding the process.**
   - On Windows: the process that owns the port and its children, as today, read for every server and not only when video memory is missing.
   - In WSL: the process whose command line names the port, or failing that the model file. `/proc` is listed at the pace of server discovery (once a minute, every five on battery); between two discoveries only the known process's `status` is read.
   - A command line can hold an API key. AI Meter keeps only the port, the model path and the offload options it recognises, and never stores, logs or returns the rest.
5. **The PC around the models.**
   - System memory in use and total; for WSL, what the virtual machine holds on Windows (`vmmemWSL`), its own total, what is available inside it and its swap; the card as today.
   - The memory attributed to models is shown inside those totals, by place: "WSL: 14.5 of 16 GB, models 11.7".
6. **An engine AI Meter does not know is still shown.**
   - A server that answers `/v1/models` and nothing more specific is listed as an OpenAI-compatible server, with the memory of its process.
   - Only when its process is found and holds memory worth a model, so that a proxy to a cloud service is not taken for a local model. The threshold is set when this is built.
7. **Segments are optional, per engine, and never guessed.**
   - Layers on the card, the key-value cache and the experts of a mixture-of-experts model are shown when the engine reports them or when they follow from the model file's header (GGUF metadata) and the loaded context.
   - An engine with its own interface gets its own kind, read from its own endpoints, even when it also imitates llama-server.
   - A segment AI Meter cannot back with a source is absent, not zero.
8. **The same data through MCP.** `list_local_models` gains these fields. Nothing existing changes meaning.

What each source gives, as known today:

| | Any engine | Ollama | llama.cpp | The engine checked |
|---|---|---|---|---|
| System memory, private and mapped, swap | measured | measured | measured | measured |
| Video memory | measured (one total for all of WSL) | reported | measured | measured, card total reported |
| Split between card and host | — | reported (`size`, `size_vram`) | layers from `-ngl` | experts on host, from options |
| Key-value cache | — | — | tokens per slot; bytes estimated from the file header | tokens and capacity; host bytes |
| Experts | — | — | count from the file header | cached slots, hit rate, bytes moved |
| Tokens per second | — | — | `/metrics`, when enabled | `/metrics` |

## Consequences

- A model that lives mostly in system memory is shown as such, on Windows and in WSL, for every engine AI Meter can find.
- AI Meter reads more of `/proc` through the WSL share, and command lines of server processes. Both stay reads of files; nothing is started in WSL.
- Video memory of one process among several in WSL stays unknown unless the engine reports it: Windows charges it all to the virtual machine.
- Estimates enter the panel. They are marked, and a wrong one is a bug to fix in the estimate, not in the measurement beside it.
- Each engine-specific reader is code to keep in step with that engine's releases. The measured base does not depend on them.
- `LocalModel` and the `list_local_models` answer grow; the taskbar pill keeps one figure.

## Phases

1. System memory per model on Windows and in WSL, and the WSL totals (decisions 1, 2 in part, 4, 5).
2. Where the model is: file size, the part on disk, swap, shared GPU memory, reported splits, origins (decisions 2, 3).
3. Segments and the unknown engine: own kinds, key-value cache, experts, tokens per second (decisions 6, 7).
4. MCP fields and warnings on memory pressure (decision 8).

## Open questions

- The engine checked reported zero occupancy of its key-value cache while a 66,000-token request ran. What that field counts must be understood before it is shown.
- The "Shared Usage" counter of "GPU Process Memory" is expected to show the spill into system memory; it has not been read yet.
- The cost of listing `/proc` through the WSL share on a busy distribution has not been measured.
- Reading another process's command line on Windows, and whether the Store build may do it.

## Alternatives considered

- **Only what engines report.** Simple and exact where it exists, but most engines report little, and each new one would show nothing until AI Meter learnt it.
- **Run `nvidia-smi` or `ps` inside WSL.** It would give more, but it starts processes in WSL at every reading and keeps the distribution awake, against ADR 0001 decision 4.
- **Charge all of `vmmemWSL` to the model.** One number with no reading inside WSL, but it counts everything else running there and cannot tell two models apart.
- **Find the process from the socket's inode.** Exact where it works, but it needs reading another process's file descriptors through the share; the command line is enough for the servers known today.
