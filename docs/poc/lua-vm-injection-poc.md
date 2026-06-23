# POC: Lua VM Injection into Darktide

# POC: Lua VM Injection into Darktide

> **Status:** Complete. All stories passed (P0 + P1). See
> `lua-vm-injection-poc-results.md` for outcomes.
>
> The POC plan document — six user stories that defined what needed to
> be proven: DLL injection, runtime function discovery, lua_State
> capture, Lua code execution, injection timing, and DMF bootstrap.
> Each story includes success criteria, references to technical docs,
> and notes from the research phase. Historical record of what was
> tested and how.
> self-contained — read the referenced docs, run the POC, write results.

---

## Read These First

Before starting, read these documents in order:

1. **`docs/poc/lua-vm-injection-theory.md`** — the full working theory.
   This is the primary reference. It covers the approach, the engine's
   Lua layer, injection mechanisms, function discovery, bootstrap
   requirements, timing analysis, and DMF's dependency surface.

2. **`docs/poc/lua-vm-injection-anchors.md`** — raw binary evidence.
   Contains PE section layout, exact file offsets of anchor strings,
   the complete list of engine function names, LuaJIT error messages,
   and the step-by-step discovery methodology. **You will need these
   offsets.**

3. **`docs/DEPLOYMENT_OPTIONS_SURVEY.md`** — broader context on why
> this approach was chosen and what the alternative (Bundle
> Virtualization) looks like. Read for background, not for POC scope.

---

## Environment

| What | Where |
|------|-------|
| Game binary | `/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe` |
| Game root | `/games/steamapps/common/Warhammer 40,000 DARKTIDE/` |
| DMF Lua source | `/games/steamapps/common/Warhammer 40,000 DARKTIDE/mods/dmf/` |
| Mod loader source | `/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/mod_loader` |
| RE docs | `the reverse-engineering documentation` |
| This project | `the project root` |

**Platform note:** The game is installed on Linux and runs via Proton.
The POC can be developed and tested on either Windows or Linux/Proton.
If developing on Linux, be aware that the injection mechanism may differ
(Wine/Proton supports CreateRemoteThread, but WINEDLLOVERRIDES is an
alternative). Use whatever is most practical for proving the concept.

---

## User Stories

Stories are ordered by dependency. Each builds on the previous. Stop
and report if a P0 story fails — there's no point continuing if the
foundation doesn't work.

---

### Story 1 (P0): Code execution inside the Darktide process

**As a researcher, I need my code running inside the Darktide process,
so that I can interact with the game's memory and the LuaJIT runtime.**

**Success criteria:**
- A DLL (or equivalent code injection) is loaded into the Darktide
  process address space
- The injected code executes and produces an observable, verifiable
  side effect (log file write, console output, file creation — anything
  that proves "my code ran inside that process")
- The game does not crash as a result of the injection

**What we're proving:** That we can get a foothold in the process at
all. Everything else depends on this.

**References:** Theory doc §4 (Injection Mechanism)

**Recommended injection mechanism for the POC: Proxy DLL (dbghelp.dll)**

Use the proxy-DLL approach, not CreateRemoteThread. The engine imports
`dbghelp.dll` from its own `binaries/` directory by search order — drop
your DLL in as `dbghelp.dll` and it loads at process startup. Under
Proton, use `WINEDLLOVERRIDES="dbghelp=native,builtin"` to force Wine
to load your version while still allowing it to reach the real builtin
dbghelp for export forwarding. (Plain `native` blocks the proxy from
loading the real `System32\dbghelp.dll` — Wine overrides are keyed on
module name, not path.)

**Why proxy DLL for the POC, not CreateRemoteThread:**
- The injection mechanism is NOT what this POC is proving. We're proving
  function discovery, lua_State capture, and Lua execution. Injection is
  just the delivery vehicle — use the simplest one.
- Proxy DLL needs no launcher process and no cross-process
  `CreateRemoteThread`. Fewer moving parts means fewer ways to fail for
  reasons unrelated to what we're testing.
- It loads at process startup via `DllMain`, which is early enough for
  all the hooks we need to install.
- The production design uses `CreateRemoteThread` (zero game-directory
  footprint) — but that's a production concern. For the POC, having a
  file in `binaries/` is fine. **The POC's proxy-DLL approach does NOT
  commit the production design to proxy DLL.** It's a POC shortcut.

**Export forwarding is a hard requirement, not optional.** `Darktide.exe`
has 13 static imports from `dbghelp.dll` (`SymCleanup`, `StackWalk64`,
`SymFunctionTableAccess64`, etc.). Unresolved imports fail at load
time — if those 13 exports aren't forwarded to the real `dbghelp.dll`,
the game never starts.

The standard fix: use a `.def` file with `EXPORTS` forwarding entries
(e.g., `SymFunctionTableAccess64 = dbghelp_real.SymFunctionTableAccess64`)
after loading the real DLL from `C:\Windows\System32\dbghelp.dll`.
This is mechanical but must be done before any other POC work can
proceed — it's part of getting the game to survive the injection, not
a nice-to-have. Treat it as a sub-task of Story 1, not a deferred item.

(Note: `GFSDK_Aftermath_Lib.x64.dll` in the same directory resolves
dbghelp dynamically — likely only on crash — so it is not a startup
risk. The engine's own static imports are what make forwarding
mandatory.)

If export forwarding proves intractable, an alternative is to use a
DLL name the engine loads but doesn't actively call during startup
(investigate the import table for candidates). The game has no
client-side anti-cheat (see deployment survey §2), so any injection
method will work.

---

### Story 2 (P0): Locate LuaJIT and engine functions in the binary

**As a researcher, I need to find the memory addresses of the LuaJIT
C API functions and the engine's LuaEnvironment functions, so that I
can hook and call them.**

**Success criteria:**
- The anchor strings from the anchors doc (§3) are located in the
  binary at runtime. At minimum: `stingray::LuaEnvironment::Internal::lua_panic`
  (offset `0x00f1f698` in the analyzed version).
- At least one engine function is found via the string-anchor → xref →
  .pdata methodology described in anchors doc §7.
- The following LuaJIT function addresses are identified:
  - `lua_newstate` (or `luaL_newstate`) — creates the VM
  - `luaL_loadstring` or `luaL_loadbuffer` — loads Lua source
  - `lua_pcall` — executes loaded code

**Verification at Story 2 is static only.** Dynamic verification
(calling the functions) is deferred to Story 3, because calling
`luaL_loadstring` or `lua_pcall` requires a `lua_State*` that doesn't
exist yet, and calling `lua_newstate` blind creates a spurious second
VM. At this stage, verify by:

- **Disassembly shape:** the discovered function's prologue and
  instruction sequence matches known LuaJIT 2.1 patterns for that
  function (compile a reference LuaJIT 2.1 binary for comparison).
- **Call-graph consistency:** the function is reachable from the
  Category-A engine anchor via the expected call chain (e.g.,
  `lua_newstate` is called from the `LuaEnvironment` init code found
  via the `lua_panic` string anchor).
- **Export/import cross-check:** if the function references known
  LuaJIT constants or error strings (see anchors doc §5), those
  references are present in the discovered function's body.

Dynamic verification happens in Story 3, where hooking the discovered
`lua_newstate` and observing the engine create its VM confirms the
address is correct.

**What we're proving:** That the function discovery methodology works
in practice, not just in theory. This is the hardest engineering
challenge in the approach.

**References:**
- Anchors doc §3 (string offsets), §4 (function name list),
  §7 (discovery methodology), §8 (PE→RVA conversion)
- Theory doc §5 (Function Discovery)

**Runtime discovery is required — do not hardcode addresses as the
primary approach.**

The point of this POC is to prove that the DLL can discover function
addresses at runtime, inside the game process. This is not a production
concern deferred to later — it is the thing we most need to validate.
Here's why:

1. **Runtime discovery is the hardest part.** If we skip it and hardcode
   addresses found offline, we've proven Lua execution works but NOT
   that we can find functions dynamically. That leaves the riskiest
   component untested.

2. **Game updates shift all addresses.** If runtime discovery doesn't
   work, the approach is fundamentally fragile in a way we need to
   know now — during POC — not during production when users hit it.

3. **The POC needs to prove end-to-end flow.** Discovery is part of
   that flow. An offline shortcut tests a different (simpler) system
   than what production needs.

**Fallback policy:** If you attempt runtime discovery and hit a wall
that blocks progress on Stories 3-4, you MAY temporarily use offline-
found addresses to unblock the rest of the POC. But this must be
clearly documented as a fallback in the results, and the runtime
discovery work should be revisited before declaring the POC complete.
The offline analysis itself can serve as a validation tool — if your
runtime-discovered addresses match offline-found ones, that confirms
both approaches are correct.

**Notes:**
- LuaJIT is **statically linked** — its functions are inside `.text`
  but not exported. They must be found by tracing from engine wrapper
  functions or by pattern scanning.
- The `.pdata` section (522 KB, ~44,600 entries) maps every function's
  start/end address. Use it to determine function boundaries.
- The LuaJIT version is `2.1.1771479498` (offset `0x00e8b108`). A
  reference LuaJIT 2.1 binary can be compiled for pattern comparison.
- It's OK if you can't find ALL functions via string-anchor tracing.
  Pattern scanning (matching known LuaJIT byte signatures) is an
  acceptable method for individual functions.

**Stories 2 and 3 are coupled in practice.** The cleanest verification
that you found the right `lua_newstate` is to hook it and watch the
engine create a VM — which is Story 3's job. Story 2's static
verification (disassembly shape, call-graph consistency) gets you to a
high-confidence candidate, but full confidence only comes when the
Story 3 hook fires and produces a valid `lua_State*`. Treat Stories 2
and 3 as one integrated discovery-and-capture phase in practice, even
though the doc lists them separately for clarity. The fallback policy
above (offline addresses to unblock) implicitly acknowledges this loop.

---

### Story 3 (P0): Capture the lua_State pointer

**As a researcher, I need to obtain the engine's `lua_State*` pointer
when the VM is created, so that I can interact with the Lua VM via the
C API.**

**Success criteria:**
- The `lua_State*` is captured at or near the time the engine creates
  its LuaEnvironment
- The pointer is non-null
- The pointer is verified as a valid `lua_State` by using it with at
  least one LuaJIT C API function (e.g., `lua_type(L, -1)` or
  `lua_gettop(L)` returns a sane value without crashing)

**What we're proving:** That we can obtain a handle to the engine's
Lua VM. Without this, we cannot load mod code.

**References:**
- Theory doc §3 (engine Lua layer), §5 (function discovery)
- Anchors doc §3 (the `lua_panic` anchor at `0x00f1f698` is the
  primary entry point for finding VM creation code)

**Notes:**
- The engine's `lua_panic` function receives `lua_State*` as its
  parameter. Finding where it's registered (via `lua_atpanic`) leads
  to the VM creation code.
- Alternatively: hook `lua_newstate` directly and capture its return
  value.
- The engine appears to use a **single** LuaEnvironment (no evidence
  of multiple VMs). If you find more than one lua_State, the correct
  one is the one where the engine's globals (`Managers`, `CLASS`,
  `require`, `print`) are registered.

---

### Story 4 (P0): Execute arbitrary Lua code in the game's VM

**As a researcher, I need to load and execute Lua code in the engine's
Lua VM from my injected DLL, so that I can load mod code.**

**Success criteria:**
- A test Lua string is loaded via `luaL_loadstring` and executed via
  `lua_pcall` using the captured `lua_State*`
- The execution produces an **observable effect inside the running
  game**. Suggested tests (pick whichever is easiest to verify):
  - `print("[INJECTED] Hello from the DLL")` — check if this appears
    in the game's console or log output
  - Setting a global variable and verifying it exists from another
    code path
  - Calling an engine function that has a visible effect (e.g., a
    notification message)
- The game does not crash during or after the execution

**What we're proving:** The fundamental capability — that we can run
arbitrary Lua code in the game's VM from an injected DLL. **If this
works, the core approach is proven.** Everything else is refinement.

**References:**
- Theory doc §6 (The Bootstrap Shim) — for understanding what globals
  to check for

**Notes:**
- At this stage, timing doesn't matter — if you can execute Lua at ANY
  point during the process lifetime, that's sufficient for P0. Precise
  timing is Story 5.
- If `luaL_loadstring` + `lua_pcall` crashes, try `luaL_dostring`
  (which is a macro for the same two calls) or `lua_load` + `lua_pcall`.
- The game's Lua VM is LuaJIT, which supports standard Lua 5.1 API
  plus JIT extensions. Standard `loadstring` / `pcall` semantics apply.

---

### Story 5 (P1): Correct injection timing

**As a researcher, I need to execute my Lua code at the right moment in
the engine's startup — after the engine's API is registered but before
the game's main loop is fully underway — so that mod code has access to
engine globals.**

**Success criteria:**
- At the time our Lua code executes, the following engine globals
  exist and are non-nil:
  - `Managers` (the engine's manager table)
  - `CLASS` (the class registry)
  - `require` (the module loading function)
  - `print` (the output function)
- Our code runs before or during the engine's script loading phase
  (before `StateGame.update` starts firing)

**What we're proving:** That we can inject at the right time, not just
at any time. This matters because DMF needs the engine's globals to
exist when it initializes.

**References:**
- Theory doc §7 (Injection Timing) — describes the window and
  detection approaches
- The current `mod_loader` (at the path in the Environment table)
  hooks `StateRequireScripts._require_scripts`, `StateGame.update`, and
  `GameStateMachine._change_state` — these are the Lua-level hooks that
  tell us about the engine's lifecycle. Our injection should happen
  before these states are reached.

**Notes:**
- Theory doc §7 suggests three detection approaches: hooking
  `lua_resource::bytecode` (first script load signals API is ready),
  polling for the `Managers` global, or hooking `main.lua` loading.
  Pick whichever is most practical.
- The string `"Lua->update"` (offset `0x00f520b8`) is the engine's
  per-frame Lua update callback. Our injection should happen before
  the first `Lua->update` call.

---

### Story 6 (P1): Bootstrap DMF's expected environment

**As a researcher, I need to set up the minimal Lua globals that DMF
expects, so that DMF's loader can initialize.**

**Success criteria:**
- The following globals are set up in the Lua VM before DMF's loader
  runs:
  - `Mods` table with `Mods.file.dofile` (a function that reads and
    executes a Lua file from disk), `Mods.lua.loadstring`,
    `Mods.lua.io`, `Mods.require_store` (empty table),
    `Mods.original_require` (the engine's `require`)
  - `__print` (set to the engine's `print`)
- DMF's `dmf_loader.lua` is loaded and begins executing without
  immediate errors (it's OK if it doesn't fully initialize — we just
  need to prove it can start)

**What we're proving:** That we can create the bridge between our
injected DLL and DMF's Lua code. DMF expects 6 things (documented in
theory doc §8); if we provide them, DMF can take over.

**References:**
- Theory doc §6 (The Bootstrap Shim) — the exact globals to set up
- Theory doc §8 (What DMF Needs From Us) — exhaustive dependency list
- `mods/dmf/scripts/mods/dmf/dmf_loader.lua` — DMF's entry point, read
  this to see what it expects

**Notes:**
- `Mods.file.dofile` can be implemented as a Lua function (using the
  engine's own `io.open` and `loadstring`) rather than a C function.
  This is simpler and more maintainable. See theory doc §6.
- DMF's full list of `Mods.*` references is exactly 6 items (verified
  by grepping all DMF Lua files). The complete list is in theory doc §8.

---

## Output: Write Your Results Here

Create a file at **`docs/poc/lua-vm-injection-poc-results.md`** in this
repo. Structure it as follows:

```markdown
# POC Results: Lua VM Injection

## Summary
[One paragraph: did the approach work? What's the verdict?]

## Story Results

### Story 1: Process Injection
- **Status:** [PASS / FAIL / PARTIAL]
- **What worked:** [brief description of how you achieved it]
- **What didn't:** [if anything]
- **Method:** [what injection method you used]

### Story 2: Function Discovery
[Same format. Include the actual function addresses you found and
the discovery method that worked for each.]

### Story 3: lua_State Capture
[Include details: how you found the state, how you verified it.]

### Story 4: Lua Code Execution
[Include the exact Lua code you executed and the observable effect.]

### Story 5: Timing (if reached)
### Story 6: Bootstrap (if reached)

## Key Findings
[Anything surprising, any gotchas, any corrections to the theory or
anchors docs. Include confirmed function addresses, working discovery
methods, and anything that differs from the documented theory.]

## Recommendations
[What should happen next if the POC succeeded. What the fallback looks
like if it didn't.]
```

**What to capture:** The end result — what worked and how it was
achieved. Not the debugging journey. Confirmed addresses, working
methods, and any corrections to the existing docs are the most valuable
outputs.

**If the existing docs (theory or anchors) contain errors you
discovered during the POC, note them in the Key Findings section.** They
will be corrected in a follow-up session.

---

## Scope Boundaries

**In scope:**
- Proving (or disproving) each story above
- Finding actual function addresses in the current binary
- Testing injection on the live game install

**Out of scope:**
- Building a production mod manager application
- Cross-platform support (Windows + Proton) — prove it on one platform
- DMF full initialization and user mod loading — Story 6 only needs to
  prove DMF's loader can START, not that it fully works
- Antivirus compatibility testing
- The Bundle Virtualization fallback — that's a separate effort
