# Phase 2a Runtime Discovery Engine — Report

> Pure offline binary analysis of `Darktide.exe`, produced by
> the C port of the Phase 0 `discover.py`. No DLL, no game
> launch, no injection. This report mirrors Phase 0's report.md
> so the two can be diffed field-by-field.

- **Binary:** `/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe`
- **Size:** 18715784 bytes (expected 18715784)
- **SHA-256:** `132eed5fe58515774a41199269dd240ef6092f84b1efc8ad4a28e23ea6791661`

## PE parse summary

| Section | RVA | File offset | Raw size | Virtual size |
|---------|-----|-------------|----------|--------------|
| `.text`  | `0x1000` | `0x600` | 15090176 | 15090047 |
| `.rdata` | `0xe68000` | `0xe65600` | 1882112 | 1881906 |
| `.pdata` | `0x26d5000` | `0x10d3600` | 523264 | 522756 |

- **.rdata RVA delta:** `0x2a00` (10752) — **matches** the documented `0x2a00`.
- **.pdata entries:** 43563 non-zero RUNTIME_FUNCTION records.

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

## Init candidate selection (Phase C)

Selected **`0x32a660`–`0x32aa2f`** (975 bytes) as the LuaEnvironment init function.

Selected as LuaEnvironment init: it takes &lua_panic (at 0x328220) via LEA at [0x32a86b] — the lua_atpanic(L, &lua_panic) setup shape. Size 975 bytes (largest candidate). 'lua_environment' string ref present in body: True.

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
- **Evidence:** lua_pcall: deferred to Phase 3 dynamic confirmation. Surveyed window [0xc73050, 0xc7fea0) around the confirmed LuaJIT API cluster (5 functions scored; 64 passed the thin-wrapper shape filter, but none cleared the high-confidence bar — no unique winner with a clear margin). lua_pcall has no string anchor and the engine's bytecode path does not exhibit a clean (L,int,int,int) call site resolving to a thin lj_docall wrapper. Phase 0 also deferred this; dynamic capture in Story 3 is the authoritative resolver.

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

## lua_pcall clustering attempt

Clustering summary: deferred (surveyed 64 cands, top score 90)

| RVA | Score | Reasoning |
|-----|------:|-----------|
| `0xc744c0` | 90 | body_size=135B, n_calls=1 (direct: 0x6845), int_args_setup=2/3 (rdx=1 r8=0 r9=1); score=90 |
| `0xc748d0` | 90 | body_size=72B, n_calls=1 (direct: 0xc82fc0), int_args_setup=2/3 (rdx=1 r8=1 r9=0); score=90 |
| `0xc74f30` | 90 | body_size=157B, n_calls=1 (direct: 0xc7ed10), int_args_setup=2/3 (rdx=0 r8=1 r9=1); score=90 |
| `0xc754d0` | 90 | body_size=133B, n_calls=1 (direct: 0xc7ed10), int_args_setup=2/3 (rdx=1 r8=0 r9=1); score=90 |
| `0xc76030` | 85 | body_size=275B, n_calls=1 (direct: 0xc756e0), int_args_setup=3/3 (rdx=1 r8=1 r9=1); score=85 |
| `0xc75340` | 75 | body_size=111B, n_calls=1 (direct: 0xc72be0), int_args_setup=2/3 (rdx=1 r8=1 r9=0); score=75 |
| `0xc76e50` | 75 | body_size=66B, n_calls=1 (direct: 0xc75840), int_args_setup=2/3 (rdx=1 r8=1 r9=0); score=75 |
| `0xc77610` | 75 | body_size=141B, n_calls=1 (direct: 0xc76f60), int_args_setup=2/3 (rdx=0 r8=1 r9=1); score=75 |

## Methodology gaps (Phase 0 findings, all handled by this engine)

1. CFG/hot-patch thunks (5-byte E9 rel32 + cc padding) have NO .pdata entry of their own — they sit in gaps between RUNTIME_FUNCTION entries. Discovery MUST follow the thunk to the real body. Observed: lua_newstate is invoked at the thunk 0xc7c000 whose real body is at 0xc7eea0.

2. Leaf functions (no prologue, SP unchanged, ret-only epilogue) are ALSO missing .pdata entries — MSVC omits them. lua_gettop (0xc74050), lua_atpanic (0xc77f40), and the lua_push* primitives (e.g. 0xc74770) all fall in .pdata gaps. .pdata is NOT a complete function map; Phase 2 must handle addresses outside .pdata by disasming bytes directly and trimming at the first ret.

3. Import thunks (FF 25 disp32 = jmp [rip+disp32]) are a third category of call target in .pdata gaps. They must be resolved through the IAT, not treated as function bodies. Example: 0xdf593c -> VCRUNTIME140.dll!memmove.

4. Phase D as documented does not work. The §5 LuaJIT error strings (attempt_to_call, bad_argument, loop_in_gettable, invalid_key_next) have ZERO LEA xrefs and ZERO pointer-table references anywhere in the binary. They are entries in a contiguous lj_err_msg[] block and are interned via lj_str_new() at VM init; thereafter referenced by GCstr* handle. Phase 2 must use a different LuaJIT-internal anchor (e.g. the lj_str_new interning loop, or a known function-body signature). Do NOT LEA-xref individual error strings.

5. lua_pcall could not be conclusively identified offline. It has no string anchor and the engine's bytecode path does not exhibit a clean (L,int,int,int) call site resolving to a thin lj_docall wrapper. Phase 2 must locate it by clustering near the other confirmed LuaJIT API functions in the 0xc7xxxx region or by structural pattern.

6. The lua_panic string anchor's containing function is lua_panic ITSELF (it logs its own name), NOT the init code. The reliable path to init is: find the lua_panic body, then find LEA references to that body's address (the &lua_panic taken for lua_atpanic). The anchors doc §7 implies the string xref lands directly in init code; it does not.

