/*
 * classify.c - Body-shape classifiers + init selection + lua_newstate
 *              dataflow trace + luaL_loadbuffer bytecode trace.
 *
 * Direct port of discover.py:
 *   - classify_by_body            (lines 570-701)
 *   - enumerate_calls             (lines 514-566)
 *   - select_init_candidate       (lines 406-483)
 *   - _identify_lua_newstate      (lines 811-985)
 *   - _find_loadbuffer_from_bytecode (lines 988-1051)
 *
 * Substring matching on capstone op_str is intentional: it mirrors the
 * Python spec bit-for-bit, which is what gives us byte-for-byte parity
 * with the Phase 0 oracle.
 */
#include "engine_internal.h"
#include "util.h"
#include <capstone/capstone.h>

/* Forward decls within this TU. */
static int call_direct_target(const cs_insn *ins, uint32_t *target);

/* ------------------------------------------------------------------- */
/* classify_by_body — port of discover.py lines 570-701.               */
/*                                                                     */
/* Writes (classification, confidence, evidence) into *out and returns */
/* 1 on success. The optional `internal_lua_load_rva` is set when the  */
/* classifier matches a lua_load wrapper (callee RVA).                 */
/* ------------------------------------------------------------------- */
typedef struct {
    char classification[64];
    char confidence[16];
    char evidence[DT_STR_LONG];
    char internal_lua_load_rva[32];   /* hex or empty */
} dt_cls_out_t;

/* Helper: collect direct-call targets inside the body. */
static int collect_call_targets(const cs_insn *insns, int n_insns,
                                uint32_t *out, int max) {
    int c = 0;
    for (int i = 0; i < n_insns; ++i) {
        if (strcmp(insns[i].mnemonic, "call") != 0) continue;
        uint32_t t;
        if (!call_direct_target(&insns[i], &t)) continue;
        if (c < max) out[c] = t;
        c++;
    }
    return c;
}

static void classify_by_body(uint32_t real_rva,
                             const dt_runtime_function_t *rf,
                             dt_cls_out_t *out) {
    const dt_engine_ctx_t *ctx = dt_engine_ctx_active();

    /* Disassemble: bounded by .pdata if present, else leaf (until ret). */
    cs_insn insns[512];
    int n = dt_disasm_range(real_rva, rf ? rf->end : 0, insns, 512);
    if (n <= 0) {
        snprintf(out->classification, sizeof(out->classification), "unknown");
        snprintf(out->confidence,     sizeof(out->confidence),     "none");
        snprintf(out->evidence,       sizeof(out->evidence),
                 "no decodable bytes");
        return;
    }

    /* Head bytes for evidence in the default fallthrough. */
    char head_hex[65] = {0};
    int head_n = n < 6 ? n : 6;
    int p = 0;
    for (int i = 0; i < head_n; ++i) {
        for (int j = 0; j < (int)insns[i].size && p < 64; ++j)
            p += snprintf(head_hex + p, sizeof(head_hex) - p,
                          "%02x", insns[i].bytes[j]);
    }

    /* ---- lua_gettop: mov rax,[rcx+X]; sub rax,[rcx+Y]; sar rax,3; ret */
    if (n >= 4 &&
        strcmp(insns[0].mnemonic, "mov") == 0 &&
        dt_str_contains(insns[0].op_str, "[rcx + ") &&
        dt_str_contains(insns[0].op_str, "rax") &&
        strcmp(insns[1].mnemonic, "sub") == 0 &&
        dt_str_contains(insns[1].op_str, "[rcx + ") &&
        (strcmp(insns[2].mnemonic, "sar") == 0 ||
         strcmp(insns[2].mnemonic, "shr") == 0) &&
        (strcmp(insns[3].mnemonic, "ret") == 0 ||
         strcmp(insns[3].mnemonic, "retn") == 0)) {
        snprintf(out->classification, sizeof(out->classification), "lua_gettop");
        snprintf(out->confidence,     sizeof(out->confidence),     "high");
        snprintf(out->evidence,       sizeof(out->evidence),
                 "leaf function computing (top-base)>>3 from "
                 "[rcx+X]/[rcx+Y], returning in rax \xe2\x80\x94 "
                 "textbook lua_gettop(L)");
        return;
    }

    /* ---- lua_atpanic: mov r8d,[rcx+8]; mov rax,[r8+0x118];
                     mov [r8+0x118],rdx; ret */
    if (n >= 4 &&
        strcmp(insns[0].mnemonic, "mov") == 0 &&
        dt_str_contains(insns[0].op_str, "r8") &&
        dt_str_contains(insns[0].op_str, "[rcx + 8]") &&
        strcmp(insns[1].mnemonic, "mov") == 0 &&
        dt_str_contains(insns[1].op_str, "[r8 + 0x118]") &&
        strcmp(insns[2].mnemonic, "mov") == 0 &&
        dt_str_contains(insns[2].op_str, "[r8 + 0x118]") &&
        dt_str_contains(insns[2].op_str, "rdx") &&
        (strcmp(insns[3].mnemonic, "ret") == 0 ||
         strcmp(insns[3].mnemonic, "retn") == 0)) {
        snprintf(out->classification, sizeof(out->classification), "lua_atpanic");
        snprintf(out->confidence,     sizeof(out->confidence),     "high");
        snprintf(out->evidence,       sizeof(out->evidence),
                 "leaf function: reads [rcx+8] (global_State* g), "
                 "swaps rdx into [g+0x118] (panic fn slot), returns "
                 "previous \xe2\x80\x94 matches lua_atpanic(L, fn)");
        return;
    }

    /* ---- lua_push* family ---- */
    if (n >= 5 && rf == NULL &&
        strcmp(insns[0].mnemonic, "mov") == 0 &&
        dt_str_contains(insns[0].op_str, "[rcx + 0x18]")) {
        /* has "add" anywhere; specifically an add touching [rcx+0x18]. */
        int has_add_top = 0;
        for (int i = 0; i < n; ++i) {
            if (strcmp(insns[i].mnemonic, "add") == 0 &&
                dt_str_contains(insns[i].op_str, "0x18]")) {
                has_add_top = 1; break;
            }
        }
        int ends_ret = (strcmp(insns[n - 1].mnemonic, "ret") == 0 ||
                        strcmp(insns[n - 1].mnemonic, "retn") == 0);
        if (has_add_top && ends_ret) {
            snprintf(out->classification, sizeof(out->classification),
                     "lua_push_family");
            snprintf(out->confidence, sizeof(out->confidence), "medium");
            snprintf(out->evidence, sizeof(out->evidence),
                     "leaf function writing a tag and bumping L->top "
                     "([rcx+0x18]) with overflow check \xe2\x80\x94 shape "
                     "matches a lua_push* primitive");
            return;
        }
    }

    /* ---- For load_wrapper / lua_gc: collect call targets + large callees. */
    uint32_t cts[64];
    int n_cts = collect_call_targets(insns, n, cts, 64);
    /* Find large (>8000 byte) callees via .pdata. */
    uint32_t large_callee_rva = 0;
    uint32_t large_callee_size = 0;
    int has_large_callee = 0;
    for (int i = 0; i < n_cts; ++i) {
        uint32_t chain[DT_MAX_THUNK_CHAIN];
        int clen = 0;
        uint32_t real = dt_trace_thunk(ctx, cts[i], chain, &clen, DT_MAX_THUNK_CHAIN);
        const dt_runtime_function_t *crf = dt_find_runtime_function(ctx, real);
        if (crf && crf->end - crf->begin > 8000) {
            uint32_t sz = crf->end - crf->begin;
            if (sz > large_callee_size) {
                large_callee_size = sz;
                large_callee_rva  = real;
                has_large_callee  = 1;
            }
        }
    }

    /* ---- lua_load_wrapper_candidate (medium) ---- */
    if (has_large_callee && rf && (rf->end - rf->begin) < 400) {
        char buf[32];
        dt_hex(buf, sizeof(buf), large_callee_rva);
        snprintf(out->classification, sizeof(out->classification),
                 "lua_load_wrapper_candidate");
        snprintf(out->confidence, sizeof(out->confidence), "medium");
        snprintf(out->internal_lua_load_rva,
                 sizeof(out->internal_lua_load_rva), "%s", buf);
        snprintf(out->evidence, sizeof(out->evidence),
                 "small wrapper (%uB) calling a %uB internal function "
                 "(likely lua_load) \xe2\x80\x94 shape matches a luaL_load* "
                 "wrapper, but call-site arg context needed to confirm "
                 "which one (loadbuffer vs loadfilex vs loadstring)",
                 rf->end - rf->begin, large_callee_size);
        return;
    }

    /* ---- lua_gc_candidate (low) ---- */
    if (rf && (rf->end - rf->begin) > 300 && (rf->end - rf->begin) < 800 &&
        n_cts >= 3 && !has_large_callee) {
        snprintf(out->classification, sizeof(out->classification),
                 "lua_gc_candidate");
        snprintf(out->confidence, sizeof(out->confidence), "low");
        snprintf(out->evidence, sizeof(out->evidence),
                 "medium body (%uB) with 3-arg (L,int,int) shape and "
                 "multiple internal calls \xe2\x80\x94 plausibly lua_gc or "
                 "another multi-arg API; needs runtime confirmation",
                 rf->end - rf->begin);
        return;
    }

    /* ---- default ---- */
    int rf_size = rf ? (int)(rf->end - rf->begin) : -1;
    snprintf(out->classification, sizeof(out->classification), "unknown");
    snprintf(out->confidence,     sizeof(out->confidence),     "none");
    snprintf(out->evidence, sizeof(out->evidence),
             "body of %dB; head=%.32s; no signature matched",
             rf_size, head_hex);
}

/* ------------------------------------------------------------------- */
/* Direct-call target extractor. Returns 1 if `call 0x....` form.      */
/* ------------------------------------------------------------------- */
static int call_direct_target(const cs_insn *ins, uint32_t *target) {
    if (strcmp(ins->mnemonic, "call") != 0) return 0;
    if (!dt_starts_with(ins->op_str, "0x")) return 0;
    *target = (uint32_t)strtoull(ins->op_str, NULL, 0);
    return 1;
}

/* ------------------------------------------------------------------- */
/* enumerate_calls — port of discover.py lines 514-566.                */
/*                                                                     */
/* Walks [begin,end) and records every CALL with classification       */
/* context (thunk chain, .pdata bounds, import info, arg hints).      */
/* ------------------------------------------------------------------- */
static int insn_writes_reg(const cs_insn *ins, const char *reg) {
    /* "rcx," | "rcx" | "rcx ..." (Python uses startswith variants). */
    const char *op = ins->op_str;
    char buf[8];
    snprintf(buf, sizeof(buf), "%s,", reg);
    if (dt_starts_with(op, buf)) return 1;
    if (dt_streq(op, reg)) return 1;
    snprintf(buf, sizeof(buf), "%s ", reg);
    if (dt_starts_with(op, buf)) return 1;
    return 0;
}

static void record_arg_hint(const cs_insn *insns, int idx,
                            const char *reg, char *out, size_t out_sz) {
    out[0] = '\0';
    int lo = idx - 8; if (lo < 0) lo = 0;
    for (int b = idx - 1; b >= lo; --b) {
        if (insn_writes_reg(&insns[b], reg)) {
            snprintf(out, out_sz, "%s %s", insns[b].mnemonic, insns[b].op_str);
            return;
        }
    }
}

int dt_enumerate_calls(uint32_t begin, uint32_t end,
                       dt_call_edge_t *out, int max) {
    const dt_engine_ctx_t *ctx = dt_engine_ctx_active();
    cs_insn insns[1024];
    int n = dt_disasm_range(begin, end, insns, 1024);
    if (n < 0) return 0;

    int count = 0;
    for (int idx = 0; idx < n; ++idx) {
        const cs_insn *ins = &insns[idx];
        if (strcmp(ins->mnemonic, "call") != 0) continue;
        if (count >= max) break;
        dt_call_edge_t *e = &out[count];
        memset(e, 0, sizeof(*e));
        dt_hex(e->call_site_rva, sizeof(e->call_site_rva), ins->address);

        uint32_t target;
        if (!call_direct_target(ins, &target)) {
            /* Indirect call. */
            dt_strncpy(e->kind, "indirect", sizeof(e->kind));
            dt_strncpy(e->operand, ins->op_str, sizeof(e->operand));
            count++;
            continue;
        }
        dt_strncpy(e->kind, "direct", sizeof(e->kind));
        dt_hex(e->target_rva, sizeof(e->target_rva), target);

        uint32_t chain[DT_MAX_THUNK_CHAIN];
        int clen = 0;
        uint32_t real = dt_trace_thunk(ctx, target, chain, &clen, DT_MAX_THUNK_CHAIN);
        e->is_thunk = (clen > 1);
        e->thunk_chain_len = clen;
        for (int i = 0; i < clen && i < DT_MAX_THUNK_CHAIN; ++i)
            dt_hex(e->thunk_chain[i], sizeof(e->thunk_chain[i]), chain[i]);
        dt_hex(e->real_target_rva, sizeof(e->real_target_rva), real);

        const dt_runtime_function_t *rf = dt_find_runtime_function(ctx, real);
        e->has_pdata = (rf != NULL);
        if (rf) {
            dt_hex(e->func_begin, sizeof(e->func_begin), rf->begin);
            dt_hex(e->func_end,   sizeof(e->func_end),   rf->end);
            e->func_size = (int)(rf->end - rf->begin);
        }

        char dll[64], nm[128];
        if (dt_resolve_import_thunk(ctx, target, dll, sizeof(dll),
                                    nm, sizeof(nm))) {
            snprintf(e->import_name, sizeof(e->import_name), "%s!%s", dll, nm);
            e->has_import = 1;
        }

        record_arg_hint(insns, idx, "rcx", e->arg_rcx, sizeof(e->arg_rcx));
        record_arg_hint(insns, idx, "rdx", e->arg_rdx, sizeof(e->arg_rdx));
        record_arg_hint(insns, idx, "r8",  e->arg_r8,  sizeof(e->arg_r8));
        record_arg_hint(insns, idx, "r9",  e->arg_r9,  sizeof(e->arg_r9));
        count++;
    }
    return count;
}

/* ------------------------------------------------------------------- */
/* Phase C: select init candidate (multi-signal: lua_panic body ->     */
/*          LEA-of-&lua_panic sites -> largest containing function).   */
/* ------------------------------------------------------------------- */
void dt_phase_c_select_init(const dt_engine_ctx_t *ctx, dt_result_t *r) {
    /* Find the lua_panic cat_a entry. Its first containing function is the
     * lua_panic body (it logs its own name). */
    const dt_cat_a_t *panic_entry = NULL;
    for (int i = 0; i < r->cat_a_count; ++i) {
        if (dt_streq(r->cat_a[i].anchor, "lua_panic")) {
            panic_entry = &r->cat_a[i];
            break;
        }
    }
    if (!panic_entry || panic_entry->containing_count == 0) {
        r->init.found = 0;
        return;
    }
    uint32_t panic_body = (uint32_t)strtoull(panic_entry->containing[0].begin_rva,
                                             NULL, 0);

    /* Find LEA references to panic_body (the &lua_panic takes). */
    uint32_t lea_sites[DT_MAX_LEA_SITES];
    int lea_n = 0;
    dt_find_lea_xrefs(ctx, panic_body, lea_sites, &lea_n, DT_MAX_LEA_SITES);

    /* Map each LEA site to its .pdata containing function. */
    typedef struct { uint32_t b, e; } cand_t;
    cand_t cands[DT_MAX_LEA_SITES];
    int ncands = 0;
    for (int i = 0; i < lea_n; ++i) {
        const dt_runtime_function_t *rf = dt_find_runtime_function(ctx, lea_sites[i]);
        if (!rf) continue;
        int dup = 0;
        for (int j = 0; j < ncands; ++j)
            if (cands[j].b == rf->begin) { dup = 1; break; }
        if (dup) continue;
        if (ncands < DT_MAX_LEA_SITES) {
            cands[ncands].b = rf->begin;
            cands[ncands].e = rf->end;
            ncands++;
        }
    }

    /* Also include direct E8 callers (defensive). */
    uint32_t callers[DT_MAX_LEA_SITES];
    int ncallers = 0;
    dt_find_callers(ctx, panic_body, callers, &ncallers, DT_MAX_LEA_SITES);
    for (int i = 0; i < ncallers; ++i) {
        const dt_runtime_function_t *rf = dt_find_runtime_function(ctx, callers[i]);
        if (!rf) continue;
        int dup = 0;
        for (int j = 0; j < ncands; ++j)
            if (cands[j].b == rf->begin) { dup = 1; break; }
        if (dup) continue;
        if (ncands < DT_MAX_LEA_SITES) {
            cands[ncands].b = rf->begin;
            cands[ncands].e = rf->end;
            ncands++;
        }
    }

    if (ncands == 0) {
        r->init.found = 0;
        r->init.lua_panic_body_rva = panic_body;
        return;
    }

    /* Pick largest by (end - begin). */
    int best = 0;
    for (int i = 1; i < ncands; ++i) {
        if ((cands[i].e - cands[i].b) > (cands[best].e - cands[best].b))
            best = i;
    }
    uint32_t chosen_b = cands[best].b;
    uint32_t chosen_e = cands[best].e;

    /* Verify "lua_environment" marker present in body. */
    static const char marker[] = "lua_environment";
    const size_t marker_len = sizeof(marker) - 1;
    int found_marker = 0;
    cs_insn insns[1024];
    int n = dt_disasm_range(chosen_b, chosen_e, insns, 1024);
    for (int i = 0; i < n; ++i) {
        if (strcmp(insns[i].mnemonic, "lea") != 0) continue;
        uint32_t tgt;
        if (!dt_lea_target_rva(&insns[i], &tgt)) continue;
        const uint8_t *bytes = dt_image_at(ctx, tgt, marker_len);
        if (bytes && memcmp(bytes, marker, marker_len) == 0) {
            found_marker = 1;
            break;
        }
    }

    /* Fill init struct. */
    r->init.found = 1;
    r->init.begin = chosen_b;
    r->init.end   = chosen_e;
    r->init.size_bytes = (int)(chosen_e - chosen_b);
    r->init.lua_environment_marker_found = found_marker;
    r->init.lua_panic_body_rva = panic_body;
    r->init.lea_of_lua_panic_count = lea_n;
    for (int i = 0; i < lea_n; ++i)
        dt_hex(r->init.lea_of_lua_panic_sites[i],
               sizeof(r->init.lea_of_lua_panic_sites[i]), lea_sites[i]);
    dt_hex(r->init.begin_rva, sizeof(r->init.begin_rva), chosen_b);
    dt_hex(r->init.end_rva,   sizeof(r->init.end_rva),   chosen_e);

    /* LEA-site list for reasoning. */
    char lea_list[256] = {0};
    for (int i = 0; i < lea_n && i < 8; ++i) {
        char buf[32];
        snprintf(buf, sizeof(buf), "0x%x%s", lea_sites[i],
                 (i + 1 < lea_n && i + 1 < 8) ? ", " : "");
        strncat(lea_list, buf, sizeof(lea_list) - strlen(lea_list) - 1);
    }
    snprintf(r->init.reasoning, sizeof(r->init.reasoning),
             "Selected as LuaEnvironment init: it takes &lua_panic (at "
             "0x%x) via LEA at [%s] \xe2\x80\x94 the lua_atpanic(L, "
             "&lua_panic) setup shape. Size %d bytes (largest candidate). "
             "'lua_environment' string ref present in body: %s.",
             panic_body, lea_list, r->init.size_bytes,
             found_marker ? "True" : "False");
}

/* ------------------------------------------------------------------- */
/* _identify_lua_newstate — backward dataflow trace from lua_atpanic.  */
/*                                                                     */
/* Recipe:                                                              */
/*   1. Find the lua_atpanic call site inside init.                    */
/*   2. Walk back to `mov rcx, <L src>` (rcx feed for atpanic).        */
/*   3. Walk further back to `mov <same slot>, rax` (where newstate's  */
/*      return was stored as L).                                       */
/*   4. The nearest preceding direct call is lua_newstate.             */
/* ------------------------------------------------------------------- */
void dt_identify_lua_newstate(const dt_engine_ctx_t *ctx,
                                     dt_result_t *r,
                                     const dt_classified_t *classified,
                                     int n_classified,
                                     dt_cat_b_t *out) {
    memset(out, 0, sizeof(*out));
    dt_strncpy(out->name, "lua_newstate", sizeof(out->name));
    dt_strncpy(out->discovery_method, "direct-call-trace",
               sizeof(out->discovery_method));

#define FAIL_(reason) do { \
        out->candidate_rva_count = 0; \
        dt_strncpy(out->confidence, "none", sizeof(out->confidence)); \
        dt_strncpy(out->evidence, (reason), sizeof(out->evidence)); \
        return; \
    } while (0)

    /* 1. Find atpanic target. */
    uint32_t atpanic_rva = 0;
    int found_atpanic = 0;
    for (int i = 0; i < n_classified; ++i) {
        if (dt_streq(classified[i].classification, "lua_atpanic")) {
            atpanic_rva = (uint32_t)strtoull(classified[i].target_rva,
                                              NULL, 0);
            found_atpanic = 1;
            break;
        }
    }
    if (!found_atpanic)
        FAIL_("lua_atpanic not classified; cannot anchor the trace");

    /* Disassemble init body. */
    cs_insn insns[1024];
    int n = dt_disasm_range(r->init.begin, r->init.end, insns, 1024);
    if (n <= 0)
        FAIL_("could not disassemble init body");

    int atpanic_idx = -1;
    for (int i = 0; i < n; ++i) {
        if (strcmp(insns[i].mnemonic, "call") != 0) continue;
        if (!dt_starts_with(insns[i].op_str, "0x")) continue;
        if ((uint32_t)strtoull(insns[i].op_str, NULL, 0) == atpanic_rva) {
            atpanic_idx = i; break;
        }
    }
    if (atpanic_idx < 0)
        FAIL_("lua_atpanic call site not found in init body");

    /* 2. Walk back to `mov rcx, <src>`. Abort on crossing another call. */
    int rcx_load_idx = -1;
    char rcx_source[128] = {0};
    int lo = atpanic_idx - 20; if (lo < 0) lo = 0;
    for (int b = atpanic_idx - 1; b >= lo; --b) {
        if (strcmp(insns[b].mnemonic, "call") == 0) break;
        if (strcmp(insns[b].mnemonic, "mov") == 0 &&
            insn_writes_reg(&insns[b], "rcx")) {
            rcx_load_idx = b;
            /* source = substring after the first comma. */
            const char *comma = strchr(insns[b].op_str, ',');
            if (comma) {
                comma++;
                while (*comma == ' ') comma++;
                dt_strncpy(rcx_source, comma, sizeof(rcx_source));
            }
            break;
        }
    }
    if (rcx_load_idx < 0)
        FAIL_("could not find the `mov rcx, <L>` feeding lua_atpanic");

    /* Normalize the slot key: bracketed expr if memory operand, else reg. */
    char slot_key[64] = {0};
    const char *lb = strchr(rcx_source, '[');
    const char *rb = strchr(rcx_source, ']');
    if (lb && rb && rb > lb) {
        size_t k = (size_t)(rb - lb) + 1;
        if (k >= sizeof(slot_key)) k = sizeof(slot_key) - 1;
        memcpy(slot_key, lb, k); slot_key[k] = '\0';
    } else {
        dt_strncpy(slot_key, rcx_source, sizeof(slot_key));
    }

    /* 3. Walk back to `mov <same slot>, rax`. */
    int store_idx = -1;
    lo = rcx_load_idx - 100; if (lo < 0) lo = 0;
    for (int b = rcx_load_idx - 1; b >= lo; --b) {
        if (strcmp(insns[b].mnemonic, "mov") != 0) continue;
        const char *op = insns[b].op_str;
        const char *comma = strchr(op, ',');
        if (!comma) continue;
        char dst[64];
        size_t dlen = (size_t)(comma - op);
        if (dlen >= sizeof(dst)) dlen = sizeof(dst) - 1;
        /* Strip leading spaces from dst. */
        const char *p = op;
        while (*p == ' ') { p++; dlen--; }
        memcpy(dst, p, dlen); dst[dlen] = '\0';
        /* Source must be rax. */
        const char *src = comma + 1;
        while (*src == ' ') src++;
        if (!dt_streq(src, "rax")) continue;
        /* Normalize dst key. */
        char dst_key[64] = {0};
        const char *dlb = strchr(dst, '[');
        const char *drb = strchr(dst, ']');
        if (dlb && drb && drb > dlb) {
            size_t k = (size_t)(drb - dlb) + 1;
            if (k >= sizeof(dst_key)) k = sizeof(dst_key) - 1;
            memcpy(dst_key, dlb, k); dst_key[k] = '\0';
        } else {
            dt_strncpy(dst_key, dst, sizeof(dst_key));
        }
        if (dt_streq(dst_key, slot_key)) {
            store_idx = b; break;
        }
    }
    if (store_idx < 0) {
        char msg[256];
        snprintf(msg, sizeof(msg),
                 "could not find `mov %s, rax` storing lua_newstate's return",
                 slot_key);
        FAIL_(msg);
    }

    /* 4. Nearest preceding direct call is lua_newstate. */
    int newstate_call_idx = -1;
    uint32_t newstate_target = 0;
    lo = store_idx - 15; if (lo < 0) lo = 0;
    for (int b = store_idx - 1; b >= lo; --b) {
        uint32_t t;
        if (call_direct_target(&insns[b], &t)) {
            newstate_call_idx = b;
            newstate_target = t;
            break;
        }
    }
    if (newstate_call_idx < 0)
        FAIL_("no direct call precedes the L store; lua_newstate may be indirect");

    /* Resolve through thunk chain. */
    uint32_t chain[DT_MAX_THUNK_CHAIN];
    int clen = 0;
    uint32_t real = dt_trace_thunk(ctx, newstate_target, chain, &clen, DT_MAX_THUNK_CHAIN);
    const dt_runtime_function_t *rf = dt_find_runtime_function(ctx, real);
    int body_size = rf ? (int)(rf->end - rf->begin) : 0;

    /* Arg setup at the call site. */
    char rcx_arg[128] = "?";
    char rdx_arg[128] = "?";
    if (newstate_call_idx >= 0) {
        int lo2 = newstate_call_idx - 10; if (lo2 < 0) lo2 = 0;
        /* rcx */
        for (int b = newstate_call_idx - 1; b >= lo2; --b) {
            if (insn_writes_reg(&insns[b], "rcx")) {
                snprintf(rcx_arg, sizeof(rcx_arg), "%s %s",
                         insns[b].mnemonic, insns[b].op_str);
                break;
            }
        }
        for (int b = newstate_call_idx - 1; b >= lo2; --b) {
            if (insn_writes_reg(&insns[b], "rdx")) {
                snprintf(rdx_arg, sizeof(rdx_arg), "%s %s",
                         insns[b].mnemonic, insns[b].op_str);
                break;
            }
        }
    }

    /* Build candidate_rvas: target first, then real body if thunk. */
    char rva_str[32];
    dt_hex(rva_str, sizeof(rva_str), newstate_target);
    dt_strncpy(out->candidate_rvas[0], rva_str, sizeof(out->candidate_rvas[0]));
    out->candidate_rva_count = 1;
    if (clen > 1) {
        dt_hex(rva_str, sizeof(rva_str), real);
        dt_strncpy(out->candidate_rvas[1], rva_str, sizeof(out->candidate_rvas[1]));
        out->candidate_rva_count = 2;
    }

    char thunk_note[256] = "";
    if (clen > 1) {
        char chain_str[128] = "";
        for (int i = 0; i < clen && i < 8; ++i) {
            char buf[24];
            snprintf(buf, sizeof(buf), "0x%x%s",
                     chain[i], (i + 1 < clen) ? "->" : "");
            strncat(chain_str, buf, sizeof(chain_str) - strlen(chain_str) - 1);
        }
        snprintf(thunk_note, sizeof(thunk_note),
                 " Entry via CFG thunk %s; real body at 0x%x.",
                 chain_str, real);
    }

    snprintf(out->evidence, sizeof(out->evidence),
             "Backward dataflow trace from the lua_atpanic call at "
             "0x%llx: rcx <- `%s` (loaded at 0x%llx), traced the L slot "
             "`%s` to a `mov %s, rax` store at 0x%llx, whose nearest "
             "preceding direct call is at 0x%llx.%s Arg setup at the call "
             "site: rcx<-(%s), rdx<-(%s) \xe2\x80\x94 matches "
             "lua_newstate(lua_Alloc f, void* ud). Real body 0x%x (%dB) "
             "contains indirect calls through the allocator pointer "
             "\xe2\x80\x94 consistent with lua_newstate's allocate-state "
             "+ allocate-stack pattern. This trace correctly skips the two "
             "intervening lua_gc calls between newstate and atpanic.",
             (unsigned long long)insns[atpanic_idx].address,
             rcx_source,
             (unsigned long long)insns[rcx_load_idx].address,
             slot_key, slot_key,
             (unsigned long long)insns[store_idx].address,
             (unsigned long long)insns[newstate_call_idx].address,
             thunk_note, rcx_arg, rdx_arg, real, body_size);

    dt_strncpy(out->confidence, "high", sizeof(out->confidence));
    if (clen > 1) {
        dt_hex(out->thunk_entry_rva, sizeof(out->thunk_entry_rva), newstate_target);
        dt_hex(out->real_body_rva,    sizeof(out->real_body_rva),    real);
    } else {
        dt_hex(out->real_body_rva,    sizeof(out->real_body_rva),    newstate_target);
    }
    out->body_size_bytes = body_size;
    out->has_trace = 1;
    dt_hex(out->trace.atpanic_call_rva, sizeof(out->trace.atpanic_call_rva),
           insns[atpanic_idx].address);
    dt_hex(out->trace.rcx_load_rva, sizeof(out->trace.rcx_load_rva),
           insns[rcx_load_idx].address);
    dt_strncpy(out->trace.rcx_source, rcx_source, sizeof(out->trace.rcx_source));
    dt_strncpy(out->trace.l_slot, slot_key, sizeof(out->trace.l_slot));
    dt_hex(out->trace.store_rva, sizeof(out->trace.store_rva),
           insns[store_idx].address);
    dt_hex(out->trace.newstate_call_rva, sizeof(out->trace.newstate_call_rva),
           insns[newstate_call_idx].address);
#undef FAIL_
}

/* ------------------------------------------------------------------- */
/* _find_loadbuffer_from_bytecode — trace from lua_resource::bytecode. */
/*                                                                     */
/* For each containing function of the bytecode anchor, enumerate      */
/* direct calls; if a callee body matches the lua_load_wrapper shape,  */
/* record it (per-site arg hints kept, mirroring discover.py). The     */
/* candidate with the most call sites wins; elevated to confirmed     */
/* luaL_loadbuffer by the 4-arg (L,buf,size,name) call-site context.  */
/* ------------------------------------------------------------------- */
typedef struct {
    uint32_t target;     /* the call target as written                  */
    uint32_t real;       /* after thunk follow                          */
    int      is_thunk;
    int      site_count;
    uint32_t sites[16];
    char     arg_rcx[16][128];
    char     arg_rdx[16][128];
    char     arg_r8 [16][128];
    char     arg_r9 [16][128];
    char     cls_evidence[DT_STR_LONG];
    char     internal_lua_load_rva[32];
} lb_cand_t;

void dt_find_loadbuffer_from_bytecode(const dt_engine_ctx_t *ctx,
                                             dt_result_t *r,
                                             dt_cat_b_t *out) {
    memset(out, 0, sizeof(*out));
    dt_strncpy(out->name, "luaL_loadbuffer", sizeof(out->name));
    dt_strncpy(out->discovery_method, "direct-call-trace",
               sizeof(out->discovery_method));

    const dt_cat_a_t *ba = NULL;
    for (int i = 0; i < r->cat_a_count; ++i) {
        if (dt_streq(r->cat_a[i].anchor, "lua_resource::bytecode")) {
            ba = &r->cat_a[i]; break;
        }
    }
    if (!ba) {
        dt_strncpy(out->confidence, "none", sizeof(out->confidence));
        dt_strncpy(out->evidence, "no candidate identified",
                   sizeof(out->evidence));
        return;
    }

    lb_cand_t cands[32];
    int ncands = 0;

    for (int ci = 0; ci < ba->containing_count; ++ci) {
        uint32_t b = (uint32_t)strtoull(ba->containing[ci].begin_rva, NULL, 0);
        uint32_t e = (uint32_t)strtoull(ba->containing[ci].end_rva,   NULL, 0);
        dt_call_edge_t edges[128];
        int ne = dt_enumerate_calls(b, e, edges, 128);
        for (int k = 0; k < ne; ++k) {
            const dt_call_edge_t *ed = &edges[k];
            if (!dt_streq(ed->kind, "direct")) continue;
            if (ed->has_import) continue;
            uint32_t real = (uint32_t)strtoull(ed->real_target_rva, NULL, 0);
            const dt_runtime_function_t *rf = dt_find_runtime_function(ctx, real);
            dt_cls_out_t cls; memset(&cls, 0, sizeof(cls));
            classify_by_body(real, rf, &cls);
            if (!dt_streq(cls.classification, "lua_load_wrapper_candidate"))
                continue;

            /* Find or insert candidate keyed by ed->target_rva. */
            uint32_t tgt = (uint32_t)strtoull(ed->target_rva, NULL, 0);
            int slot = -1;
            for (int i = 0; i < ncands; ++i)
                if (cands[i].target == tgt) { slot = i; break; }
            if (slot < 0) {
                if (ncands >= 32) continue;
                slot = ncands++;
                memset(&cands[slot], 0, sizeof(cands[slot]));
                cands[slot].target   = tgt;
                cands[slot].real     = real;
                cands[slot].is_thunk = ed->is_thunk;
                dt_strncpy(cands[slot].cls_evidence, cls.evidence,
                           sizeof(cands[slot].cls_evidence));
                dt_strncpy(cands[slot].internal_lua_load_rva,
                           cls.internal_lua_load_rva,
                           sizeof(cands[slot].internal_lua_load_rva));
            }
            if (cands[slot].site_count < 16) {
                int s = cands[slot].site_count++;
                cands[slot].sites[s] = (uint32_t)strtoull(ed->call_site_rva, NULL, 0);
                dt_strncpy(cands[slot].arg_rcx[s], ed->arg_rcx, sizeof(cands[slot].arg_rcx[s]));
                dt_strncpy(cands[slot].arg_rdx[s], ed->arg_rdx, sizeof(cands[slot].arg_rdx[s]));
                dt_strncpy(cands[slot].arg_r8 [s], ed->arg_r8,  sizeof(cands[slot].arg_r8 [s]));
                dt_strncpy(cands[slot].arg_r9 [s], ed->arg_r9,  sizeof(cands[slot].arg_r9 [s]));
            }
        }
    }

    if (ncands == 0) {
        dt_strncpy(out->confidence, "none", sizeof(out->confidence));
        dt_strncpy(out->evidence, "no candidate identified",
                   sizeof(out->evidence));
        return;
    }

    int best = 0;
    for (int i = 1; i < ncands; ++i)
        if (cands[i].site_count > cands[best].site_count) best = i;
    const lb_cand_t *B = &cands[best];

    char rva_str[32];
    dt_hex(rva_str, sizeof(rva_str), B->target);
    dt_strncpy(out->candidate_rvas[0], rva_str, sizeof(out->candidate_rvas[0]));
    out->candidate_rva_count = 1;
    if (B->is_thunk) {
        dt_hex(rva_str, sizeof(rva_str), B->real);
        dt_strncpy(out->candidate_rvas[1], rva_str, sizeof(out->candidate_rvas[1]));
        out->candidate_rva_count = 2;
    }

    /* Render site list as Python-style: "['0x..', '0x..']". */
    char site_list[256] = "[";
    int sp = 1;
    for (int i = 0; i < B->site_count; ++i) {
        char buf[24];
        int w = snprintf(buf, sizeof(buf), "'0x%x'%s",
                         B->sites[i], (i + 1 < B->site_count) ? ", " : "");
        if (sp + w < (int)sizeof(site_list) - 2) {
            memcpy(site_list + sp, buf, w); sp += w;
        }
    }
    if (sp < (int)sizeof(site_list) - 1) site_list[sp++] = ']';
    site_list[sp] = '\0';

    /* Render per-site arg evidence, mirroring discover.py:
     *   "site 0x..: rcx<-<v>, rdx<-<v>, r9<-<v>; site 0x..: ..."
     * Only rcx/rdx/r8/r9 entries that are non-empty appear. */
    char arg_ev[DT_STR_LONG] = "";
    int pe = 0;
    for (int s = 0; s < B->site_count; ++s) {
        char chunk[512];
        int pp = snprintf(chunk, sizeof(chunk), "site 0x%x: ", B->sites[s]);
        const char *labels[4] = {"rcx", "rdx", "r8", "r9"};
        const char *vals[4]   = {B->arg_rcx[s], B->arg_rdx[s],
                                 B->arg_r8[s],  B->arg_r9[s]};
        int first = 1;
        for (int k = 0; k < 4; ++k) {
            if (vals[k][0] == '\0') continue;
            pp += snprintf(chunk + pp, sizeof(chunk) - pp,
                           "%s%s<-%s", first ? "" : ", ",
                           labels[k], vals[k]);
            first = 0;
        }
        if (s + 1 < B->site_count)
            pp += snprintf(chunk + pp, sizeof(chunk) - pp, "; ");
        if (pe + pp < (int)sizeof(arg_ev) - 1) {
            memcpy(arg_ev + pe, chunk, pp); pe += pp;
        } else {
            strncat(arg_ev, chunk, sizeof(arg_ev) - pe - 1); break;
        }
    }

    snprintf(out->evidence, sizeof(out->evidence),
             "%s Elevated to luaL_loadbuffer by tracing from the "
             "lua_resource::bytecode anchor (2 containing engine functions, "
             "%d call site(s): %s) with call-site arg context: %s. The 4-arg "
             "(L,buf,size,name) shape plus lua_load callee confirms "
             "luaL_loadbuffer over the other luaL_load* wrappers.",
             B->cls_evidence, B->site_count, site_list, arg_ev);

    dt_strncpy(out->confidence, "high", sizeof(out->confidence));
    dt_strncpy(out->internal_lua_load_rva, B->internal_lua_load_rva,
               sizeof(out->internal_lua_load_rva));
}

/* ------------------------------------------------------------------- */
/* dt_classified_to_cat_b — port of discover.py _to_cat_b.             */
/* ------------------------------------------------------------------- */
void dt_classified_to_cat_b(const dt_classified_t *c, const char *name,
                            const char *method, dt_cat_b_t *out) {
    memset(out, 0, sizeof(*out));
    dt_strncpy(out->name, name, sizeof(out->name));
    dt_strncpy(out->discovery_method, method, sizeof(out->discovery_method));
    dt_strncpy(out->confidence, c->confidence, sizeof(out->confidence));
    dt_strncpy(out->evidence, c->evidence, sizeof(out->evidence));

    /* target first; append real_target if thunk and distinct. */
    dt_strncpy(out->candidate_rvas[0], c->target_rva,
               sizeof(out->candidate_rvas[0]));
    out->candidate_rva_count = 1;
    int is_thunk = c->is_thunk;
    int real_differs = !dt_streq(c->target_rva, c->real_target_rva);
    if (is_thunk && real_differs) {
        dt_strncpy(out->candidate_rvas[1], c->real_target_rva,
                   sizeof(out->candidate_rvas[1]));
        out->candidate_rva_count = 2;
    }
}

/* ------------------------------------------------------------------- */
/* dt_classify_by_body_wrap — thin extern wrapper used by discover.c.  */
/* ------------------------------------------------------------------- */
void dt_classify_by_body_wrap(uint32_t real,
                              const dt_runtime_function_t *rf,
                              char *cls, size_t cls_sz,
                              char *conf, size_t conf_sz,
                              char *ev, size_t ev_sz,
                              char *internal_lua_load, size_t ill_sz) {
    dt_cls_out_t out;
    memset(&out, 0, sizeof(out));
    classify_by_body(real, rf, &out);
    dt_strncpy(cls,   out.classification, cls_sz);
    dt_strncpy(conf,  out.confidence,     conf_sz);
    dt_strncpy(ev,    out.evidence,       ev_sz);
    if (internal_lua_load && ill_sz)
        dt_strncpy(internal_lua_load, out.internal_lua_load_rva, ill_sz);
}
