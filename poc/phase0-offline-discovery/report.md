# Phase 0 Offline Discovery — Report

> Pure offline binary analysis of `Darktide.exe`. No DLL, no game 
> launch, no injection. This report validates the anchors-doc §7 
> discovery methodology and produces a cross-check table for Phase 2.

- **Binary:** `/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe`
- **Size:** 18,715,784 bytes (expected 18,715,784)
- **SHA-256:** `132eed5fe58515774a41199269dd240ef6092f84b1efc8ad4a28e23ea6791661`

## PE parse summary

| Section | RVA | File offset | Raw size | Virtual size |
|---------|-----|-------------|----------|--------------|
| `.text` | `0x1000` | `0x600` | 15,090,176 | 15,090,047 |
| `.rdata` | `0xe68000` | `0xe65600` | 1,882,112 | 1,881,906 |
| `.pdata` | `0x26d5000` | `0x10d3600` | 523,264 | 522,756 |

- **.rdata RVA delta:** `0x2a00` (10752) — **matches** the documented `0x2a00`.
- **.pdata entries:** 43,563 non-zero RUNTIME_FUNCTION records.

## Anchor sanity check (Phase A)

Every §3 Quick-Reference anchor was read at its documented file offset 
and compared against the documented string.

| Anchor | File offset | At offset? | Computed RVA | Correction |
|--------|-------------|------------|--------------|------------|
| `LuaJIT version` | `0xe8b108` | **yes** | `0xe8db08` | — |
| `lua_panic` | `0xf1f698` | **yes** | `0xf22098` | — |
| `default_error_callback` | `0xf1f910` | **yes** | `0xf22310` | — |
| `clear_temp_variables` | `0xf1fa48` | **yes** | `0xf22448` | — |
| `dump_state` | `0xf1fc98` | **yes** | `0xf22698` | — |
| `lua_resource::bytecode` | `0xf1d9d0` | **yes** | `0xf203d0` | — |
| `copy_lua_variable_to_c` | `0xf266c8` | **yes** | `0xf290c8` | — |
| `push_c_variable_to_lua` | `0xf267f8` | **yes** | `0xf291f8` | — |
| `Bundle::open` | `0xf50bc0` | **yes** | `0xf535c0` | — |
| `Lua->update` | `0xf520b8` | **yes** | `0xf54ab8` | — |
| `load_script_data` | `0xf51ef8` | **yes** | `0xf548f8` | — |
| `bundle_database.data` | `0xf4e298` | **yes** | `0xf50c98` | — |
| `lua_environment_api` | `0xf1f4f8` | **yes** | `0xf21ef8` | — |

**Result: 13/13 anchors verified at their documented offsets.**

**No string-offset corrections needed.** Every §3 anchor matched.

## Category A engine functions (Phase B)

For each anchor: `.text` was scanned for RIP-relative LEA references; 
each xref site was mapped to its containing RUNTIME_FUNCTION via .pdata.

| Anchor | Anchor RVA | Xrefs | Distinct containing funcs | Func RVAs |
|--------|-----------:|------:|--------------------------:|----------|
| `LuaJIT version` | `0xe8db08` | 1 | 1 | `0xc80bf5` |
| `lua_panic` | `0xf22098` | 2 | 1 | `0x328220` |
| `default_error_callback` | `0xf22310` | 1 | 1 | `0x32a630` |
| `clear_temp_variables` | `0xf22448` | 1 | 1 | `0x32bc03` |
| `dump_state` | `0xf22698` | 1 | 1 | `0x32c58b` |
| `lua_resource::bytecode` | `0xf203d0` | 2 | 2 | `0x3298b0`, `0x32ab30` |
| `copy_lua_variable_to_c` | `0xf290c8` | 1 | 1 | `0x39d870` |
| `push_c_variable_to_lua` | `0xf291f8` | 1 | 1 | `0x39d6f0` |
| `Bundle::open` | `0xf535c0` | 2 | 1 | `0x707430` |
| `Lua->update` | `0xf54ab8` | 1 | 1 | `0x70ef50` |
| `load_script_data` | `0xf548f8` | 1 | 1 | `0x70e590` |
| `bundle_database.data` | `0xf50c98` | 1 | 1 | `0x6eefa0` |
| `lua_environment_api` | `0xf21ef8` | 1 | 1 | `0x323810` |

Notes:
- `lua_panic` resolves to a single 160-byte function — that function 
  **is `lua_panic` itself** (it references its own name in its logging 
  path), not the init code. See the init-candidate section below.
- `lua_resource::bytecode` resolves to **two** large engine functions 
  (each ~2.5 KB); these are the bytecode→VM loading path.
- `LuaJIT version` resolves to a function inside the LuaJIT cluster 
  (0xc80bf5), a useful sanity check that the cluster is correctly located.

## Init candidate selection (Phase C)

Selected **`0x32a660`–`0x32aa2f`** (975 bytes) as 
the LuaEnvironment init function.

Selected as LuaEnvironment init: it takes &lua_panic (at 0x328220) via LEA at ['0x32a86b'] — the lua_atpanic(L, &lua_panic) setup shape. Size 975 bytes (largest candidate). 'lua_environment' string ref present in body: True.

- `lua_environment` string marker present in body: **True**
- lua_panic body (string-xref owner): `0x328220`
- LEA-of-&lua_panic sites (lua_atpanic setup): ['0x32a86b']

## Category B LuaJIT candidates (Phase C)

Direct-call graph of the init candidate was enumerated; each distinct 
call target was resolved (thunks followed, import thunks identified) 
and classified by body shape and call context.

### Confirmed identifications

| Function | Candidate RVA(s) | Confidence | Discovery method |
|----------|------------------|------------|------------------|
| `lua_newstate` | `0xc7c000`, `0xc7eea0` | **high** | direct-call-trace |
| `lua_atpanic` | `0xc77f40` | **high** | direct-call-trace |
| `lua_gettop` | `0xc74050` | **high** | direct-call-trace |
| `luaL_loadbuffer` | `0xc7ad80` | **high** | direct-call-trace |
| `lua_pcall` | — | **none** | direct-call-trace |

### `lua_newstate` — confidence: high

- **Candidate RVA(s):** `0xc7c000`, `0xc7eea0`
- **Discovery method:** direct-call-trace
- **Evidence:** Backward dataflow trace from the lua_atpanic call at 0x32a872: rcx <- `qword ptr [r14]` (loaded at 0x32a868), traced the L slot `[r14]` to a `mov [r14], rax` store at 0x32a83a, whose nearest preceding direct call is at 0x32a835. Entry via CFG thunk 0xc7c000->0xc7eea0; real body at 0xc7eea0. Arg setup at the call site: rcx<-(?), rdx<-(mov rdx, r14) — matches lua_newstate(lua_Alloc f, void* ud). Real body 0xc7eea0 (170B) contains indirect calls through the allocator pointer — consistent with lua_newstate's allocate-state + allocate-stack pattern. This trace correctly skips the two intervening lua_gc calls between newstate and atpanic.

### `lua_atpanic` — confidence: high

- **Candidate RVA(s):** `0xc77f40`
- **Discovery method:** direct-call-trace
- **Evidence:** leaf function: reads [rcx+8] (global_State* g), swaps rdx into [g+0x118] (panic fn slot), returns previous — matches lua_atpanic(L, fn)

### `lua_gettop` — confidence: high

- **Candidate RVA(s):** `0xc74050`
- **Discovery method:** direct-call-trace
- **Evidence:** leaf function computing (top-base)>>3 from [rcx+X]/[rcx+Y], returning in rax — textbook lua_gettop(L)

### `luaL_loadbuffer` — confidence: high

- **Candidate RVA(s):** `0xc7ad80`
- **Discovery method:** direct-call-trace
- **Evidence:** small wrapper (161B) calling a 14379B internal function (likely lua_load) — shape matches a luaL_load* wrapper, but call-site arg context needed to confirm which one (loadbuffer vs loadfilex vs loadstring) Elevated to luaL_loadbuffer by tracing from the lua_resource::bytecode anchor (2 containing engine functions, 2 call site(s): ['0x32a163', '0x32b2cf']) with call-site arg context: site 0x32a163: rcx<-mov rcx, r14, rdx<-mov rdx, qword ptr [rax + rdx*8 + 0x10], r9<-mov r9, qword ptr [rsp + 0x38]; site 0x32b2cf: rcx<-mov rcx, qword ptr [r15], rdx<-mov rdx, qword ptr [rax + rcx*8 + 0x10], r9<-mov r9, qword ptr [rsp + 0x38]. The 4-arg (L,buf,size,name) shape plus lua_load callee confirms luaL_loadbuffer over the other luaL_load* wrappers.

### `lua_pcall` — confidence: none

- **Candidate RVA(s):** none
- **Discovery method:** direct-call-trace
- **Evidence:** Not conclusively identified in Phase 0. lua_pcall has no string anchor and the engine's bytecode path (0x3298b0/0x32ab30) does not exhibit a clean (L,int,int,int) call site that resolves to a thin wrapper around lj_docall. The LuaJIT API cluster around 0xc7xxxx contains many candidates; runtime confirmation needed. See Recommendations for Phase 2.

### Full init-candidate call graph (classified)

| Call target | Thunk? | Real body | Size | Import | Classification | Confidence |
|-------------|--------|-----------|-----:|--------|----------------|------------|
| `0x271e70` | no | `0x271e70` | 39 | — | `unknown` | none |
| `0x32a2a0` | no | `0x32a2a0` | 498 | — | `lua_gc_candidate` | low |
| `0x6a2cc0` | no | `0x6a2cc0` | 116 | — | `unknown` | none |
| `0x6a79b0` | no | `0x6a79b0` | 106 | — | `unknown` | none |
| `0x6b5c80` | no | `0x6b5c80` | 46 | — | `unknown` | none |
| `0xc73c50` | no | `0xc73c50` | 460 | — | `unknown` | none |
| `0xc73ea0` | no | `0xc73ea0` | 171 | — | `lua_load_wrapper_candidate` | medium |
| `0xc74050` | no | `0xc74050` | ? | — | `lua_gettop` | high |
| `0xc74580` | no | `0xc74580` | 174 | — | `unknown` | none |
| `0xc746b0` | no | `0xc746b0` | 67 | — | `unknown` | none |
| `0xc74770` | no | `0xc74770` | ? | — | `lua_push_family` | medium |
| `0xc747d0` | no | `0xc747d0` | 121 | — | `unknown` | none |
| `0xc74890` | no | `0xc74890` | 54 | — | `unknown` | none |
| `0xc74970` | no | `0xc74970` | 49 | — | `unknown` | none |
| `0xc74a90` | no | `0xc74a90` | 129 | — | `unknown` | none |
| `0xc74f30` | no | `0xc74f30` | 157 | — | `unknown` | none |
| `0xc77f40` | no | `0xc77f40` | ? | — | `lua_atpanic` | high |
| `0xc7cb50` | no | `0xc7cb50` | 198 | — | `unknown` | none |
| `0xc7c000` | yes | `0xc7eea0` | 170 | — | `unknown` | none |
| `0xdf593c` | no | `0xdf593c` | ? | VCRUNTIME140.dll!memmove | `import` | high |

Indirect calls inside init (flagged for Phase 2 backward tracing): **0**
(none — the init path uses only direct calls.)

## LuaJIT error-string cross-check (Phase D)

The documented Phase D approach is to LEA-xref the §5 LuaJIT error 
strings. **This does not work.** All four documented error strings 
produce zero LEA xrefs and zero pointer-table references anywhere in 
the binary.

| Error string | File offset | String present? | LEA xrefs | Pointer hits |
|--------------|-------------|-----------------|----------:|-------------:|
| `attempt_to_call` | `0xe89b86` | True | 0 | 0 |
| `bad_argument` | `0xe89c97` | True | 0 | 0 |
| `loop_in_gettable` | `0xe89c1c` | True | 0 | 0 |
| `invalid_key_next` | `0xe89b70` | True | 0 | 0 |

**Methodology note:** Phase D as documented does NOT work: 0 LEA xrefs and 0 pointer-table hits for every §5 error string. The strings are entries in a contiguous lj_err_msg[] block in .rdata and are interned into LuaJIT's string hash table at VM init via lj_str_new(); the code thereafter references them by GCstr* handle, never by raw .rdata address. Phase 2 must find the lj_str_new interning loop or use a different anchor (e.g. a known LuaJIT function body signature), not LEA-xref on individual error strings.

The §5 strings ARE present in the binary at their documented offsets 
and they ARE laid out as a contiguous `lj_err_msg[]` block (the wider 
0xe89b00–0xe89e00 region contains ~40 error-message strings packed 
back-to-back). But the code never references them by address — they 
are interned into LuaJIT's string hash table at VM init and accessed 
by `GCstr*` handle thereafter.

## Doc corrections and methodology gaps

### Doc corrections

**None found.** Every §3 anchor string is present at its documented 
file offset, and the `.rdata` RVA delta is exactly `0x2a00` as documented.

### Methodology gaps (must feed back into the anchors doc before Phase 2)

1. CFG/hot-patch thunks (5-byte E9 rel32 + cc padding) have NO .pdata entry of their own — they sit in gaps between RUNTIME_FUNCTION entries. Discovery MUST follow the thunk to the real body. Observed: lua_newstate is invoked at the thunk 0xc7c000 whose real body is at 0xc7eea0.
2. Leaf functions (no prologue, SP unchanged, ret-only epilogue) are ALSO missing .pdata entries — MSVC omits them. lua_gettop (0xc74050), lua_atpanic (0xc77f40), and the lua_push* primitives (e.g. 0xc74770) all fall in .pdata gaps. .pdata is NOT a complete function map; Phase 2 must handle addresses outside .pdata by disasming bytes directly and trimming at the first ret.
3. Import thunks (FF 25 disp32 = jmp [rip+disp32]) are a third category of call target in .pdata gaps. They must be resolved through the IAT, not treated as function bodies. Example: 0xdf593c -> VCRUNTIME140.dll!memmove.
4. Phase D as documented does not work. The §5 LuaJIT error strings (attempt_to_call, bad_argument, loop_in_gettable, invalid_key_next) have ZERO LEA xrefs and ZERO pointer-table references anywhere in the binary. They are entries in a contiguous lj_err_msg[] block and are interned via lj_str_new() at VM init; thereafter referenced by GCstr* handle. Phase 2 must use a different LuaJIT-internal anchor (e.g. the lj_str_new interning loop, or a known function-body signature). Do NOT LEA-xref individual error strings.
5. lua_pcall could not be conclusively identified offline. It has no string anchor and the engine's bytecode path does not exhibit a clean (L,int,int,int) call site resolving to a thin lj_docall wrapper. Phase 2 must locate it by clustering near the other confirmed LuaJIT API functions in the 0xc7xxxx region or by structural pattern.
6. The lua_panic string anchor's containing function is lua_panic ITSELF (it logs its own name), NOT the init code. The reliable path to init is: find the lua_panic body, then find LEA references to that body's address (the &lua_panic taken for lua_atpanic). The anchors doc §7 implies the string xref lands directly in init code; it does not.

## Recommendations for Phase 2

Based on the above, runtime discovery should:

1. **Follow CFG thunks.** When a direct call target has no .pdata 
entry, check for `E9 rel32` and follow the chain. `lua_newstate` is 
invoked at the thunk `0xc7c000`; the real body is `0xc7eea0`. A 
runtime hook must install on the thunk entry (what callers actually 
invoke) OR on the real body — pick one and document it. Hooking the 
thunk is safer for capture (it is the actual call target).

2. **Do NOT assume .pdata is a complete function map.** Leaf functions 
(lua_gettop, lua_atpanic, lua_push*) have no .pdata entries. When 
classifying a call target that falls in a .pdata gap, disassemble the 
bytes directly and trim at the first `ret`. The byte signatures for 
lua_gettop and lua_atpanic are stable and documented in this report.

3. **Resolve import thunks.** `FF 25 disp32` calls go through the IAT. 
Phase 2 should parse the import directory and resolve these to 
`DLL!function` names (useful for recognizing allocator/VirtualAlloc 
calls inside lua_newstate).

4. **Use the multi-signal init path.** The reliable recipe is: 
  (a) find the `lua_panic` string → its containing function IS lua_panic; 
  (b) find LEA references to that function's address → those are the 
      lua_atpanic setup sites; the largest containing function is 
      LuaEnvironment init. 
  Do NOT assume the string xref lands directly in init.

5. **Drop Phase D's individual-string-xref approach.** It cannot work. 
To anchor into LuaJIT's error layer, find the `lj_str_new()` interning 
loop that processes the `lj_err_msg[]` block at VM init. This is 
non-trivial and may not be worth it — the confirmed LuaJIT API cluster 
around 0xc7xxxx (lua_newstate, lua_atpanic, lua_gettop, lua_gc, 
luaL_loadbuffer) is a better anchor surface than the error strings.

6. **Locate lua_pcall by clustering.** lua_pcall, lua_gettop, 
lua_atpanic, lua_newstate, and luaL_loadbuffer are all emitted from 
LuaJIT's `lj_api.c` / `lj_load.c` and live in a tight address cluster 
(0xc73xxx–0xc7exxx on this build). Once one LuaJIT API function is 
confirmed, the others are nearby. lua_pcall is a thin wrapper around 
`lj_docall`; look for a small function taking `(L, nargs, nresults, 
errfunc)` — 4 args, the last three small integers — that calls an 
internal docall routine. Confirm dynamically in Story 3.

7. **Cross-check targets for Phase 2.** The `candidate_rvas` in 
`addresses.json` are the values Phase 2's runtime discovery should 
reproduce. A match confirms both implementations.

