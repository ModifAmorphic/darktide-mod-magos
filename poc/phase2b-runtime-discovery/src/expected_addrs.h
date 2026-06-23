/* expected_addrs.h — Phase 0 cross-check constants baked into the DLL.
 *
 * These are the 7 confirmed LuaJIT/engine function addresses from
 * poc/phase0-offline-discovery/addresses.json, expressed as RVAs
 * (offsets from the Darktide.exe module base). The worker thread compares
 * each discovered address against EXPECT_<name> and logs MATCH or MISMATCH.
 *
 * EXPECT_LUA_PCALL is the Phase 4 re-identification (source-pattern match
 * against LuaJIT 2.1 lj_api.c — see phase4-execute-lua/report.md). The
 * discovery worker does NOT autonomously re-derive lua_pcall (no offline
 * anchor exists), so it remains absent from kExpectedAddrs[] below — but
 * the constant is exposed here as a single source of truth for inject.c
 * and for offline tests (the A1 disasm-check asserts the shape).
 *
 * These constants are pinned to the analyzed Darktide.exe build
 * (SHA-256 132eed5f...791661, 18,715,784 bytes). A game update will shift
 * every address; runtime discovery is what makes the approach survive
 * updates, and a MISMATCH against these baked-in values is the signal that
 * the build moved (Phase 3 would then re-derive against the new binary).
 */
#ifndef DARKTIDE_POC_EXPECTED_ADDRS_H
#define DARKTIDE_POC_EXPECTED_ADDRS_H

#include <stdint.h>

#define EXPECT_LUA_PANIC_BODY   0x328220u   /* stingray::LuaEnvironment::Internal::lua_panic body */
#define EXPECT_INIT_BEGIN       0x32a660u   /* LuaEnvironment init (begin) */
#define EXPECT_NEWSTATE_THUNK   0xc7c000u   /* lua_newstate thunk (what callers invoke) */
#define EXPECT_NEWSTATE_BODY    0xc7eea0u   /* lua_newstate real body (after CFG-thunk follow) */
#define EXPECT_ATPANIC          0xc77f40u   /* lua_atpanic */
#define EXPECT_GETTOP           0xc74050u   /* lua_gettop */
#define EXPECT_LOADBUFFER       0xc7ad80u   /* luaL_loadbuffer */
/* lua_pcall: Phase 4 re-identification (source-pattern match against
 * lj_api.c:1120). The function at this RVA reads all 4 args (L, nargs,
 * nresults, errfunc), computes errfunc→stack-offset via savestack, computes
 * api_call_base = L->top - nargs*8, increments nresults, and tail-calls
 * lj_vm_pcall. Confirmed offline; not in kExpectedAddrs[] because the
 * discovery worker has no offline anchor for it. */
#define EXPECT_LUA_PCALL        0xc744c0u

/* luaL_openlibs: Phase 5 Step 1 identification (source-pattern match against
 * LuaJIT 2.1 lib_init.c). The function at this RVA is a small (0xc2-byte)
 * function with two loops:
 *   loop 1: for each entry in lj_lib_load[], call lua_pushcfunction +
 *           lua_pushstring + lua_call (registers base/package/table/io/
 *           os/string/math/debug/bit/jit)
 *   between: luaL_findtable(L, LUA_REGISTRY_INDEX, "_PRELOAD", 1)
 *   loop 2: for each entry in lj_lib_preload[], call lua_pushcfunction +
 *           lua_setfield (registers ffi)
 *   cleanup: tail-jump to lua_settop (lua_pop(L, 1))
 * Match evidence: LEA r8,[rip+0x20e295] targets the "_PRELOAD" string
 * (rva 0xe8d678); 6 distinct call targets = {0xc74580 (pushcclosure),
 * 0xc747d0 (pushstring), 0xc738e0 (lua_call), 0xc7c250 (findtable),
 * 0xc74cb0 (setfield), 0xc74f30 (settop)}. Verified by the A1
 * disasm_check_luaL_openlibs() regression gate. Confirmed offline; not in
 * kExpectedAddrs[] (no offline anchor for the discovery worker). */
#define EXPECT_LUAL_OPENLIBS    0xc7f380u

/* lua_pushcclosure: Phase 5 Step 3 (C-function bootstrap) identification
 * (source-pattern match against LuaJIT 2.1 lj_api.c:678). NOTE:
 * `lua_pushcfunction` is a macro in lua.h (`#define lua_pushcfunction(L,f)
 * lua_pushcclosure(L, (f), 0)`); it has NO symbol in the binary. The actual
 * function is `lua_pushcclosure(L, f, n)` and is what we call with n=0 to
 * register a C function. The function at this RVA is the 0xae-byte body
 * whose distinctive features are:
 *   - `movsxd rdi, r8d`            ; sign-extend n (the nups arg)
 *   - 1 backward `jne`             ; the `while (n--)` upvalue-copy loop
 *   - writes `0xfffffff7` to a tag ; setfuncV — LJ_TFUNC = 0xF7 low byte
 *   - exactly 3 direct `call`s (lj_gc_check @ 0xc82fc0, lj_func_newC @
 *     0xc85460, lj_state_growstack @ 0xc7ede0)
 * Verified by the A1 disasm_check_lua_pushcclosure() regression gate.
 * Unique match in the 219-entry cluster [0xc73000, 0xc80000). */
#define EXPECT_LUA_PUSHCCLOSURE 0xc74580u

/* lua_setfield: Phase 5 Step 3 (C-function bootstrap) identification
 * (source-pattern match against LuaJIT 2.1 lj_api.c:970). NOTE:
 * `lua_setglobal` is a macro in lua.h (`#define lua_setglobal(L,s)
 * lua_setfield(L, LUA_GLOBALSINDEX, (s))`); it has NO symbol in the
 * binary. We call `lua_setfield(L, -10002, name)` directly to register
 * a global (LUA_GLOBALSINDEX = -10002 = 0xFFFFD8EE in LuaJIT 2.1; the
 * engine's own init at 0x32a2a0 uses this exact constant 13 times).
 * The function at this RVA is the 0x76-byte body whose distinctive
 * features are:
 *   - 3-arg prologue: saves `rcx`->`rsi` (L) and `r8`->`rbx` (k);
 *     does NOT save `edx` (idx — consumed by the first call)
 *   - first `call` is `index2adr(L, idx)` at 0xc72be0 (edx still = idx)
 *   - writes `0xfffffffb` to a tag slot ; setstrV key (LJ_TSTR = 0xFB)
 *   - ends with `lea rcx, [rdx-8]; mov [rsi+0x18], rcx` ; L->top-- (pop)
 *   - exactly 4 direct `call`s (index2adr, lj_str_newz, lj_tab_setkey,
 *     lj_tab_set)
 * Verified by the A1 disasm_check_lua_setfield() regression gate.
 * Unique match in the 219-entry cluster [0xc73000, 0xc80000). */
#define EXPECT_LUA_SETFIELD     0xc74cb0u

/* lua_pushstring (BONUS — Phase 5 Step 3 identification). Not strictly
 * needed for the C-function bootstrap (poc_print takes no args, returns
 * nothing) but documented for future use (returning strings from C
 * functions). Source-pattern match against LuaJIT 2.1 lj_api.c:647.
 * Distinctive features:
 *   - `test rdx, rdx` + `jne`       ; the `if (str == NULL)` check
 *   - writes BOTH `0xffffffff` (NIL tag, setnilV) AND `0xfffffffb`
 *     (STR tag, setstrV) — the two branches of the if
 *   - exactly 4 direct `call`s (lj_gc_check, lj_str_newz, setstrV helper,
 *     lj_state_growstack)
 * Unique match in the 219-entry cluster. */
#define EXPECT_LUA_PUSHSTRING   0xc747d0u

/* lua_tolstring: Phase 5 (DMF bootstrap) identification (source-pattern
 * match against LuaJIT 2.1 lj_api.c:493). Reads a string argument from
 * the Lua stack — needed by c_dofile / c_loadstring to read the path/
 * source passed from Lua. Signature:
 *   const char *lua_tolstring(lua_State *L, int idx, size_t *len)
 * Returns NULL if the value at idx is not a string (or number-coercible);
 * otherwise returns the C-string and (if len != NULL) writes its length.
 * The function at this RVA is the body whose distinctive features are:
 *   - 3-arg prologue (rcx=L, edx=idx, r8=len*) — saves L→rsi, idx→rbx,
 *     len*→rdi
 *   - calls index2adr (0xc72be0) up to TWICE with lj_gc_check (0xc82fc0)
 *     between them (the "GC may move the stack" re-read path)
 *   - calls lj_strfmt_number (0xc89700) on the number-coercion path
 *   - the "else" branch (not str/num) does `test rdi,rdi; jne; mov [rdi],0`
 *     (`if (len != NULL) *len = 0; return NULL`)
 *   - the success path ends with `add rax, 0x14` (strdata(s) = s + 0x14;
 *     sizeof(GCstr) = 0x14 in LJ_64 non-GC64 builds) followed by `ret`
 *   - body size ~0x88 bytes
 * Unique match in the cluster. Verified by A1 disasm_check_lua_tolstring. */
#define EXPECT_LUA_TOLSTRING    0xc75190u

/* lua_createtable: Phase 5 (DMF bootstrap) identification (source-pattern
 * match against LuaJIT 2.1 lj_api.c:708). Creates an empty table and
 * pushes it on the stack — needed to build the `Mods` table and its
 * subtables. Signature:
 *   void lua_createtable(lua_State *L, int narray, int nrec)
 * Source body:
 *   lj_gc_check(L);
 *   settabV(L, L->top, lj_tab_new_ah(L, narray, nrec));
 *   incr_top(L);
 * The function at this RVA is the small (~0x6a-byte) body whose
 * distinctive features are:
 *   - 3-arg prologue (rcx=L, edx=narray, r8=nrec) — saves L→rbx,
 *     narray→esi (ebp in some builds), nrec→edi
 *   - conditional call to lj_gc_check (0xc82fc0)
 *   - exactly 2 direct `call`s: lj_gc_check + lj_tab_new_ah (0xc84510)
 *     (plus a conditional call to lj_state_growstack at 0xc7ede0 in
 *     incr_top's bounds check)
 *   - writes LJ_TTAB tag (0xfffffff4 — low byte 0xF4) via
 *     `mov dword [reg+0x4], 0xfffffff4`
 *   - ends with `add [L+0x18], 8` (incr_top) + bounds check
 * Verified by A1 disasm_check_lua_createtable. */
#define EXPECT_LUA_CREATETABLE  0xc73ad0u

/* lua_type: Phase 5 (DMF bootstrap) identification (source-pattern match
 * against LuaJIT 2.1 lj_api.c:222). Returns the Lua type of the value at
 * idx as an int (LUA_TSTRING=4, LUA_TNUMBER=3, etc.). Useful for
 * type-checking arguments in C functions. Signature:
 *   int lua_type(lua_State *L, int idx)
 * The function at this RVA is the small (~0x70-byte) body whose
 * distinctive features are:
 *   - calls index2adr (0xc72be0) exactly once
 *   - checks tag for number (0xfffeffff range), lightud (0xffff0000),
 *     niltv (g+0xc0)
 *   - uses the MAGIC CONSTANT `0x75a0698042110` (movabs rax, ...) as a
 *     lookup table for itype→Lua-type conversion
 *     (lower 4 bytes = 0x98042110, upper = 0x00075a06)
 *   - returns int in eax
 *   - body size ~0x70 bytes
 * Verified by A1 disasm_check_lua_type. */
#define EXPECT_LUA_TYPE         0xc753b0u

/* lua_tonumber: Phase 5 (DMF bootstrap) identification (source-pattern
 * match against LuaJIT 2.1 lj_api.c:351). Returns the value at idx as a
 * double (in xmm0); returns 0 if not convertible. Useful for reading
 * numeric arguments. Signature:
 *   lua_Number lua_tonumber(lua_State *L, int idx)
 * The function at this RVA is the small (~0x60-byte) body whose
 * distinctive features are:
 *   - 2-arg (rcx=L, edx=idx) — saves L→rdi, idx→ebx
 *   - calls index2adr (0xc72be0) once
 *   - checks tag for number (0xfffeffff range)
 *   - if string (0xfffffffb), calls lj_strscan_num (0xc886e0)
 *   - returns via `movsd xmm0, [rax]` (number) or `movsd xmm0, [rsp+0x40]`
 *     (parsed-from-string value)
 *   - body size ~0x60 bytes
 * Verified by A1 disasm_check_lua_tonumber. */
#define EXPECT_LUA_TONUMBER     0xc730c0u

/* One entry in the cross-check table. `actual_getter` reads the discovered
 * RVA out of the dt_result_t; the field paths differ per function so we
 * keep the lookup logic in discover_worker.c rather than here. */
typedef struct {
    const char *label;       /* log label, e.g. "lua_newstate_body" */
    uint32_t    expected;    /* baked-in RVA from Phase 0 */
} expected_addr_t;

/* The ordered cross-check list (matches the RUNBOOK's success criteria).
 * lua_newstate appears twice (thunk + body); discover_worker.c knows how
 * to resolve each from the result struct. */
static const expected_addr_t kExpectedAddrs[] = {
    { "lua_panic_body",       EXPECT_LUA_PANIC_BODY },
    { "init_begin",           EXPECT_INIT_BEGIN     },
    { "lua_newstate_thunk",   EXPECT_NEWSTATE_THUNK },
    { "lua_newstate_body",    EXPECT_NEWSTATE_BODY  },
    { "lua_atpanic",          EXPECT_ATPANIC        },
    { "lua_gettop",           EXPECT_GETTOP         },
    { "luaL_loadbuffer",      EXPECT_LOADBUFFER     },
};
#define kExpectedAddrsCount \
    (sizeof(kExpectedAddrs) / sizeof(kExpectedAddrs[0]))

#endif /* DARKTIDE_POC_EXPECTED_ADDRS_H */
