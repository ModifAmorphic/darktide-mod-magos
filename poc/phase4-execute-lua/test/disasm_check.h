/*
 * disasm_check.h — Tier A1 offline assertion that the RVA we baked in for
 * lua_pcall actually points at lua_pcall in the game binary.
 *
 * The A1 mock-VM test (test/a1_inject_test.c) exercises the inject.c
 * detour against the SYSTEM LuaJIT's lua_pcall. That proves the detour
 * logic is correct, but it does NOT prove the address we hooked in the
 * game binary is actually lua_pcall. This module closes that gap:
 *
 *   1. Open Darktide.exe (PE-x86-64) from a configurable path.
 *   2. Map RVA → file offset via the PE section table.
 *   3. Capstone-disassemble ~40 instructions starting at the RVA.
 *   4. Match the disassembly against lua_pcall's source-compiled shape
 *      (see report.md §"lua_pcall re-identification"):
 *        - reads  [rcx + 0x08]   (glref → global_State*)
 *        - reads  [rcx + 0x10]   (L->base)
 *        - reads  [rcx + 0x18]   (L->top)
 *        - reads  [rcx + 0x24]   (L->stack, MRef)
 *        - test r9d, r9d         (errfunc == 0 check)
 *        - jne/jle pair          (the positive/negative errfunc branches)
 *        - lea reg, [reg+r9*8]   (errfunc → TValue* slot arithmetic)
 *        - inc r8d               (nresults + 1)
 *        - sub rdx, rax after shl rax, 3   (api_call_base = L->top - nargs*8)
 *        - exactly one direct `call rel32`
 *
 * Returns 0 on match, nonzero on any mismatch (with diagnostics printed
 * to stdout). The A1 test makes this a hard gate.
 *
 * Capstone is linked as a native static lib (the same vendored capstone
 * the Phase 2a engine uses, built for Linux in phase2-runtime-discovery/
 * vendor/capstone/build/libcapstone.a). The check is portable C99.
 */
#ifndef DARKTIDE_POC_PHASE4_DISASM_CHECK_H
#define DARKTIDE_POC_PHASE4_DISASM_CHECK_H

#include <stdint.h>     /* uint32_t */

/* Verify the function at `rva` in the PE file `pe_path` has lua_pcall's
 * source-compiled shape. Returns 0 on match, nonzero otherwise.
 *
 * `pe_path` may be NULL — in that case the default install location
 * (/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe)
 * is used, which is correct on the dev box.
 *
 * `rva` is the RVA to check (typically EXPECT_LUA_PCALL from
 * expected_addrs.h — 0xc744c0).
 *
 * On failure, prints the failing criterion + the first ~40 decoded
 * instructions to stdout for diagnosis.
 */
int disasm_check_lua_pcall(const char *pe_path, uint32_t rva);

/* Verify the function at `rva` in the PE file `pe_path` has luaL_openlibs's
 * source-compiled shape (Phase 5 Step 1 identification). Returns 0 on match,
 * nonzero otherwise. Same `pe_path` semantics as disasm_check_lua_pcall().
 *
 * Distinctive features required (see EXPECT_LUAL_OPENLIBS in
 * expected_addrs.h for the full evidence):
 *   - LEA r, [rip+disp32] targeting the "_PRELOAD" string (rva 0xe8d678)
 *   - At least 5 distinct direct `call rel32` targets (the loop bodies
 *     call pushcfunction, pushstring, lua_call, findtable, setfield)
 *   - At least one `jne` backward branch (loop back-edge)
 *   - Function size <= 0x200 bytes (openlibs is a small function)
 *   - All call targets within the LuaJIT API cluster (0xc7xxxx)
 *
 * On failure, prints the failing criterion + the first ~40 decoded
 * instructions to stdout for diagnosis.
 */
int disasm_check_luaL_openlibs(const char *pe_path, uint32_t rva);

/* Verify the function at `rva` has lua_pushcclosure's source-compiled shape
 * (Phase 5 Step 3 — C-function bootstrap identification). Returns 0 on
 * match, nonzero otherwise. Same `pe_path` semantics.
 *
 * NOTE: `lua_pushcfunction` is a macro (`#define lua_pushcfunction(L,f)
 * lua_pushcclosure(L, (f), 0)`) — the binary symbol is `lua_pushcclosure`.
 * This is what we call to push a C function onto the Lua stack.
 *
 * Distinctive features (LuaJIT 2.1 lj_api.c:678):
 *   - `movsxd` of `r8d`            ; sign-extend n (the nups arg)
 *   - at least one backward `jne`   ; the `while (n--)` upvalue-copy loop
 *   - writes `0xfffffff7` (LJ_TFUNC tag) to a TValue slot
 *   - exactly 3 direct `call rel32` (lj_gc_check, lj_func_newC,
 *     lj_state_growstack)
 *   - body size <= 0x100 bytes
 */
int disasm_check_lua_pushcclosure(const char *pe_path, uint32_t rva);

/* Verify the function at `rva` has lua_setfield's source-compiled shape
 * (Phase 5 Step 3 — C-function bootstrap identification). Returns 0 on
 * match, nonzero otherwise. Same `pe_path` semantics.
 *
 * NOTE: `lua_setglobal` is a macro (`#define lua_setglobal(L,s)
 * lua_setfield(L, LUA_GLOBALSINDEX, (s))`) — the binary symbol is
 * `lua_setfield`. We call `lua_setfield(L, -10002, name)` directly to
 * register a Lua global (LUA_GLOBALSINDEX = -10002 in LuaJIT 2.1).
 *
 * Distinctive features (LuaJIT 2.1 lj_api.c:970):
 *   - 3-arg prologue: saves `rcx`->`rsi` (L) AND `r8`->`rbx` (k);
 *     does NOT save `edx` (idx — consumed by first call to index2adr)
 *   - writes `0xfffffffb` (LJ_TSTR tag for the key TValue)
 *   - ends with `lea rcx, [rdx-8]; mov [rsi+0x18], rcx` ; L->top-- (pop)
 *   - exactly 4 direct `call rel32` (index2adr, lj_str_newz, lj_tab_setkey,
 *     lj_tab_set)
 *   - body size <= 0x100 bytes
 */
int disasm_check_lua_setfield(const char *pe_path, uint32_t rva);

#endif /* DARKTIDE_POC_PHASE4_DISASM_CHECK_H */
