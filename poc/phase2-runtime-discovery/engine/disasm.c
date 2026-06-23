/*
 * disasm.c - Capstone wrapper + leaf-function disasm + RIP-target extract.
 *
 * Phase 0's discover.py uses capstone's Python bindings; this is the C
 * port. We use the Intel-syntax printer (CS_OPT_SYNTAX_INTEL) so the
 * emitted op_str matches the Python's "lea rax, [rip + 0x1234]" style
 * exactly, which lets us port the substring-based classifiers and the
 * backward dataflow trace verbatim.
 */
#include "engine_internal.h"
#include "util.h"

/* Open a capstone handle configured for our use. Returns CS_ERR_OK (0)
 * on success, a non-zero cs_err on failure. Callers treat any non-zero
 * return as a hard failure (the previous code returned CS_ERR_OK on the
 * cs_open failure path, which silently masked init errors). */
static int open_cs(csh *h) {
    cs_err rc = cs_open(CS_ARCH_X86, CS_MODE_64, h);
    if (rc != CS_ERR_OK) return rc;
    cs_option(*h, CS_OPT_SYNTAX, CS_OPT_SYNTAX_INTEL);
    cs_option(*h, CS_OPT_DETAIL, CS_OPT_OFF);   /* op_str is enough */
    return cs_errno(*h);
}

int dt_disasm_range(uint32_t begin, uint32_t end,
                    cs_insn *insns, int max_insns) {
    const dt_engine_ctx_t *ctx = dt_engine_ctx_active();
    if (!ctx) return -1;

    int is_leaf = (end == 0);
    if (is_leaf) {
        /* Decode up to 256 bytes; caller (or this loop) trims at ret. */
        end = begin + 256;
    }
    if (end <= begin) return 0;
    size_t want = end - begin;
    const uint8_t *code = dt_image_at(ctx, begin, want);
    if (!code) return -1;

    csh h;
    if (open_cs(&h) != CS_ERR_OK) return -1;

    size_t code_len = want;
    uint64_t addr = begin;
    const uint8_t *cur = code;
    int n = 0;

    if (is_leaf) {
        /* Linear sweep; stop at first ret/retn, cap at max_insns. */
        while (n < max_insns) {
            cs_insn *one = NULL;
            size_t got = cs_disasm(h, cur, code_len, addr, 1, &one);
            if (got == 0) { if (one) cs_free(one, 1); break; }
            insns[n++] = *one;            /* copy by value */
            const char *m = one->mnemonic;
            int is_ret = (strcmp(m, "ret") == 0 || strcmp(m, "retn") == 0);
            cs_free(one, 1);
            if (is_ret) break;
            cur     += insns[n - 1].size;
            code_len -= insns[n - 1].size;
            addr    += insns[n - 1].size;
            if (code_len == 0) break;
        }
    } else {
        /* Decode the entire [begin,end) range linearly. */
        cs_insn *all = NULL;
        size_t got = cs_disasm(h, code, code_len, begin, 0, &all);
        int take = (int)got;
        if (take > max_insns) take = max_insns;
        for (int i = 0; i < take; ++i) insns[i] = all[i];
        n = take;
        if (all) cs_free(all, got);
    }

    cs_close(&h);
    return n;
}

int dt_disasm_raw(uint32_t begin, uint32_t len,
                  cs_insn *insns, int max_insns) {
    const dt_engine_ctx_t *ctx = dt_engine_ctx_active();
    if (!ctx) return -1;
    const uint8_t *code = dt_image_at(ctx, begin, len);
    if (!code) return -1;
    csh h;
    if (open_cs(&h) != CS_ERR_OK) return -1;
    cs_insn *all = NULL;
    size_t got = cs_disasm(h, code, len, begin, 0, &all);
    int take = (int)got;
    if (take > max_insns) take = max_insns;
    for (int i = 0; i < take; ++i) insns[i] = all[i];
    if (all) cs_free(all, got);
    cs_close(&h);
    return take;
}

/* Extract RIP-relative target RVA from a LEA/MOV/etc. instruction.
 *
 * Phase 0 parses capstone's op_str ("lea rax, [rip + 0x1234]") with
 * split("rip + "). We mirror that exactly to guarantee parity. Returns
 * 1 on success (and writes the target RVA), 0 if not RIP-relative.
 */
int dt_lea_target_rva(const cs_insn *ins, uint32_t *target) {
    const char *op = ins->op_str;
    if (!op) return 0;
    const char *plus  = strstr(op, "rip + ");
    const char *minus = strstr(op, "rip - ");
    long long disp;
    if (plus) {
        disp = strtoll(plus + strlen("rip + "), NULL, 0);
    } else if (minus) {
        disp = -strtoll(minus + strlen("rip - "), NULL, 0);
    } else {
        return 0;
    }
    /* On x86-64, RIP-relative address = next_insn_addr + disp.
     * next_insn_addr = ins->address + ins->size. */
    uint64_t t = (uint64_t)ins->address + ins->size + (int64_t)disp;
    *target = (uint32_t)t;
    return 1;
}
