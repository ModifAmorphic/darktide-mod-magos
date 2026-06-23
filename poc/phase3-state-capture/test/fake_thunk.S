/*
 * fake_thunk.S — CFG-thunk-shaped fake target for A1.
 *
 * Models the live game's `lua_newstate` thunk at RVA 0xc7c000, which is:
 *
 *     0xc7c000:  E9 <rel32>         ; jmp <body>
 *     0xc7c005:  CC CC CC ...       ; int3 padding (CFG thunk convention)
 *
 * A pure `jmp rel32 + padding` is the hard shape for inline hooking: the
 * entire function is one instruction, so the hook library must relocate
 * that jmp to its trampoline and adjust the displacement to land at the
 * same body from a new location. MinHook handles this (E8/E9 are special-
 * cased in trampoline.c). This file constructs that exact shape so A1
 * proves the mechanism works on the target's real shape.
 *
 * The body (`thunk_body`) returns 0xDEADBEEFCAFEBABE — the sentinel the
 * detour captures and passes through.
 *
 * Symbol map (x64 System V AMD64 ABI == MS x64 ABI for leaf functions):
 *   fake_newstate_thunk_impl_entry   — public thunk entry (the call target)
 *   thunk_body                        — private impl body (jumped to)
 *
 * Layout & call path:
 *   caller -> fake_newstate_thunk_impl_entry: jmp thunk_body
 *   thunk_body: movabs $0xDEADBEEFCAFEBABE, %rax; ret
 *
 * When hooked: caller -> detour -> trampoline -> thunk_body -> detour -> caller.
 * The detour captures %rax on the way back.
 *
 * NOTE on directives: PE/COFF (mingw) assemblers do not honor GNU `.type`/
 * `.size`/`.section .note.GNU-stack` directives. A `.globl` symbol emitted
 * into `.text` is treated as a function by the linker automatically. We are
 * modeling a LuaJIT CFG thunk, which itself has no unwind info (CFG thunks
 * are not in .pdata; they're not regular functions) — so we deliberately
 * emit NO `.seh_*` directives.
 */

    .text

    .globl fake_newstate_thunk_impl_entry
fake_newstate_thunk_impl_entry:
    /* The thunk: a 5-byte jmp rel32, then int3 padding. */
    jmp     thunk_body
    .fill   16, 1, 0xcc             /* int3 padding (CFG-thunk convention) */

    .globl thunk_body
thunk_body:
    /* Body: return 0xDEADBEEFCAFEBABE in %rax.
     * rcx (lua_Alloc) and rdx (void *ud) are ignored — the real
     * lua_newstate uses them; the fake doesn't. */
    movabsq $0xDEADBEEFCAFEBABE, %rax
    ret
