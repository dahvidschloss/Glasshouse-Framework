
# Glasshouse Framework

Glasshouse is a post-exploitation research framework focused on Chromium DevTools Protocol (CDP) exposure in real-world desktop environments. The core research objective is to evaluate how attacker activity could be blended into legitimate browser and Electron application behavior, and to identify the defensive visibility gaps that make this possible.

This work builds on prior CDP security research from the Emulated Criminals team, including [SilentFrame](https://github.com/Emulated-Criminals/SilentFrame/). Glasshouse extends that line of research from browser-only scenarios into broader Chromium-based application ecosystems, with emphasis on Electron-backed clients where CDP availability and operational assumptions are often under-reviewed.

The framework is motivated by a practical trend. Modern desktop software is increasingly built on web stacks (for example Next.js + Electron), and shipping velocity often outpaces hardening. AI-assisted coding workflows can reinforce this trend by repeatedly favoring high-productivity patterns and starter architectures that lead teams toward Next.js for application logic and Electron for desktop packaging. In many teams, these tools optimize first for "working code" and fast iteration, not for threat modeling or secure-by-default runtime configuration.

This does not imply AI-generated code is uniquely unsafe, but it can mirror the same behavior commonly seen in junior engineering, prioritizing functionality before security controls are fully understood. Without experienced security review, this can unintentionally preserve or introduce straightforward weaknesses, such as exposed debugging interfaces, permissive defaults, weak environment separation, and insufficient runtime hardening. Glasshouse is designed to study that gap directly by evaluating how those seemingly small implementation decisions can become practical abuse paths when CDP surfaces are reachable.

Glasshouse is currently structured as two components:

1. `ChatterBox`: Interactive CLI for CDP enumeration, profile creation, and repeatable command workflow development.
2. `Glasshouse` (WIP): Planned post-exploitation operator module intended to execute profile-driven actions through target CDP channels.


# ChatterBox CLI

ChatterBox is an interactive command-line client for CDP, and serves as the enumeration and operator-control arm of the broader Glasshouse framework.

At a framework level, Glasshouse is focused on working with Chromium-based debug surfaces (browsers and Electron-style desktop apps) through CDP. ChatterBox is the interface used to discover available protocol capability, inspect runtime state, and build repeatable command workflows that can be reused across targets and engagements.

In practical terms, ChatterBox bridges two worlds:

- Low-level CDP access (`Domain.method` calls, raw JSON, context/event inspection)
- High-level operator workflow (profiles, templated commands, cached outputs, interactive completion)

This makes it useful both for direct manual exploration and for scaling repeated tasks into named commands that stay consistent between sessions.

ChatterBox connects to a running Chromium-based target (browser or Electron app exposing CDP), lets you send CDP methods directly, and adds quality-of-life features for repetitive workflows:

- Domain-aware command shortcuts
- Protocol-driven `help` and tab completion
- Profile commands (templated macros with parameters)
- In-memory and persisted output caches
- Multiple output modes (table, result JSON, full JSON)

This document is intentionally detailed and can be used as a full help reference.

## Table of Contents

1. What ChatterBox Is
2. Requirements
3. Build and Run
4. Connection Model
5. Input Model (How Commands Are Parsed)
6. Built-In Commands
7. Sending CDP Calls
8. Domains and Contexts
9. Profiles and Custom Commands
10. Cache System
11. Tab Completion and History
12. Output Modes
13. File Layout and Persistence
14. Examples
15. Troubleshooting

## 1) What ChatterBox Is

ChatterBox is an interactive REPL-style CDP console.
You type commands; it sends JSON-RPC messages over a WebSocket to a CDP endpoint and prints responses/events.

Core design goals:

- Fast manual CDP exploration
- Fast repeatability with profile macros
- Better UX than raw JSON-only interactions

## 2) Requirements

- .NET SDK 9.0 (project targets `net9.0`)
- A target exposing CDP (for example Chromium with remote debugging enabled)
- Network access from your machine to the target CDP HTTP/WebSocket endpoint

## 3) Build and Run

From the `Chatterbox` directory:

```powershell
dotnet build
dotnet run
```

Optional startup arguments:

- `--wsUrl <ws://...>`: explicit websocket debugger URL
- `--cdpAddress <host>`: CDP HTTP host (default `127.0.0.1`)
- `--cdpPort <port>`: CDP HTTP port (default `9222`)

Examples:

```powershell
dotnet run -- --cdpAddress 127.0.0.1 --cdpPort 9222
dotnet run -- --wsUrl ws://127.0.0.1:9222/devtools/page/<id>
```

On startup you should see:

- `ChatterBox CLI ready. Type 'connect' to attach or 'help' for commands.`

## 4) Connection Model

ChatterBox can resolve CDP WebSocket URL in two ways:

1. Direct: use `--wsUrl`
2. Auto-resolve from HTTP endpoint:
   - tries `http://<address>:<port>/json/list` first (prefers `type == "page"`)
   - falls back to `http://<address>:<port>/json/version`

### Typical workflow

1. Configure endpoint (`set.address`, `set.port`) if needed
2. `connect`
3. Send CDP methods
4. `disconnect` when done

On connect, ChatterBox also:

- Loads protocol metadata from `/json/protocol` (if available)
- Enables `Runtime`, `Log`, and `Page` domains automatically

## 5) Input Model (How Commands Are Parsed)

For non-local commands, ChatterBox treats input as:

`<method> <params>`

Where `<params>` can be:

- JSON object text: `{"key":"value"}`
- Dash params: `-key value -flag`

If no params are provided, it sends method-only.

### Method name resolution

- If method already includes dot (`Runtime.evaluate`), it is used as-is
- If method has no dot and a current domain is set (for example `Runtime`), ChatterBox prefixes it automatically (for example `evaluate` becomes `Runtime.evaluate`)

## 6) Built-In Commands

### Connection

- `connect`
- `disconnect`
- `set.address <addr>`
- `set.port <port>`
- `info`

### Help

- `help`
- `help <Domain>`
- `help <Domain>.<Command>`
- `help <Command>` (uses current domain)
- `help profile <subcommand>`

### Domain and discovery

- `list domains`
- `domain <Name>`
- `domain clear`
- `list targets`
- `list contexts`
- `searchCDP` (only meaningful while disconnected)

### Profiles

- `profile create <name>`
- `profile load <name>`
- `profile unload`
- `profile list`
- `profile show`
- `profile command add ...`
- `profile command modify ...`
- `profile command remove ...`
- `profile cache save`

### Cache

- `cache list`
- `cache show <name>`

### Output and logging

- `output.psobj` (table-like formatting)
- `output.json` (result JSON)
- `output.fulljson` (full response JSON)
- `input.show`
- `input.hide`

### Raw and shell-like convenience

- `raw <json>`
- `clear`
- `quit` / `exit`

## 7) Sending CDP Calls

### JSON object style

```text
Runtime.evaluate {"expression":"2+2"}
```

### Dash-parameter style

```text
Runtime.evaluate -expression "2+2"
Network.enable
Page.navigate -url "https://example.com"
```

Dash param conversion rules:

- `true`/`false` parse as booleans
- integer-like text parses as integer
- numeric text parses as floating number
- JSON-looking text (`{...}` / `[...]`) parses to JSON element
- otherwise treated as string

You can also set per-call cache output in method mode:

```text
Runtime.evaluate -expression "JSON.stringify(['a','b'])" -cacheOutput roots
```

(`-cacheOutput` is consumed by ChatterBox and not sent to CDP.)

## 8) Domains and Contexts

### Domains

- `list domains` shows known protocol domains (loaded after connect)
- `domain Runtime` sets current domain
- then `evaluate -expression "1+1"` maps to `Runtime.evaluate`
- `domain clear` removes prefix behavior

You can also type a domain name directly; if it matches, ChatterBox treats that as `domain <Name>`.

### Context tracking

ChatterBox tracks runtime execution contexts from events:

- `Runtime.executionContextCreated`
- `Runtime.executionContextDestroyed`
- `Runtime.executionContextsCleared`

Use `list contexts` to print known contexts in a table.

## 9) Profiles and Custom Commands

Profiles let you define reusable command templates with placeholders.

### Create/load profile

```text
profile create myprofile
profile load myprofile
profile unload
profile list
profile show
```

### Add/modify/remove commands

Supported `function` flags:

- `-function`
- `-func`

Syntax:

```text
profile command add -name <name> -function <template> [-desc <text>] [-cacheOutput <name>]
profile command modify -name <name> -function <template> [-desc <text>] [-cacheOutput <name>]
profile command remove -name <name>
```

### Placeholder syntax in templates

Format:

- `$name` required parameter
- `$name?` optional parameter
- `$name:&cacheName` value autocomplete source from cache
- `$name?:&cacheName` optional + cache-linked

Example template:

```text
Runtime.evaluate -expression "JSON.stringify(window.$root:&windowRoots)"
```

Run it:

```text
myCmd -root someRootName
```

Expansion behavior:

- Missing required placeholder -> command is rejected with guidance
- Optional placeholder not provided -> placeholder token is removed from template
- If command has exactly one placeholder and you provide exactly one positional value, it auto-binds:
  - `myCmd foo` behaves like `myCmd -placeholder foo` (for single-placeholder templates)

### Profile command cacheOutput

If a profile command defines `cacheOutput`, its extracted list result is stored under that cache name when the response arrives.
At runtime you can override with:

```text
myProfileCmd ... -cacheOutput otherName
```

### Default commands on new profile

`profile create` currently seeds a small default set:

- `enum_WindowRoots`
- `enum_RootFuncs`

These are saved in the new profile JSON and can be modified/removed like any other profile command.

## 10) Cache System

ChatterBox keeps an in-memory dictionary of caches: `name -> list of strings`.

Population sources:

- CDP method sends with `cacheOutput`
- profile commands with `cacheOutput`
- loaded profile persisted caches

Use:

- `cache list`
- `cache show <name>`

Persist current in-memory caches into active profile:

```text
profile cache save
```

## 11) Tab Completion and History

### Completion

`Tab` completion supports:

- Local command names
- Domain names
- Domain command names (`Runtime.evaluate` etc.)
- Methods in current domain (short names)
- Method parameter names after `-` (from protocol metadata)
- Profile command names
- Profile placeholder parameter names (`-root`, etc.)
- Cached value completions when placeholders declare `:&cacheName`

If multiple matches exist:

- ChatterBox tries to extend to best/common prefix
- If no further single prefix is possible, it prints candidates

### History and line editing

Supported:

- Up/Down: navigate command history
- Left/Right/Home/End: cursor movement
- Inline editing and redraw-safe output while async messages arrive

## 12) Output Modes

### `output.psobj` (default)

Table-like formatter for common JSON structures:

- arrays of scalars -> single-column table
- arrays of objects -> multi-column table
- nested objects/arrays -> readable sections

### `output.json`

Pretty-printed `result` payload.

### `output.fulljson`

Pretty-printed entire CDP response/event message.

### Input logging

- `input.show` prints outbound payload as `[IN] ...`
- `input.hide` disables it

## 13) File Layout and Persistence

Profile storage directory is created under:

- `<AppContext.BaseDirectory>/profiles`

When running with `dotnet run`, this commonly resolves to build output folders such as:

- `bin/Debug/net9.0/profiles`

Each profile is saved as lowercase `<name>.json`.

## 14) Examples

### Basic session

```text
set.address 127.0.0.1
set.port 9222
connect
list domains
domain Runtime
evaluate -expression "2+2"
disconnect
```

### Define a reusable profile command

```text
profile create demo
profile command add -name eval -function 'Runtime.evaluate -expression "$expr"' -desc "Run expression"
eval -expr "document.title"
```

### Cache values and reuse in completion

```text
profile command add -name getRoots -function 'Runtime.evaluate -expression "JSON.stringify(Object.keys(window))"' -cacheOutput roots
getRoots
cache list
cache show roots
```

## 15) Troubleshooting

### `Not connected. Use 'connect' first.`

- Run `connect` first
- Confirm target exposes CDP (`/json/version` reachable)

### `Unknown domain`

- Run `list domains` after connecting
- Domain names must match loaded protocol names

### `Missing -function` while adding profile command

- Use `-function` or `-func`
- Wrap complex template in quotes

### No `help` details for protocol commands

- Protocol metadata loads only after successful connect
- If `/json/protocol` is unavailable, only local help remains available

### `searchCDP` behavior

- Intended for disconnected state; scans local listening TCP ports and checks for CDP response

---

## Security / Responsible Use

This tool can execute powerful browser/Electron automation and inspection commands via CDP.
Use only on systems and targets you are authorized to test or operate.
