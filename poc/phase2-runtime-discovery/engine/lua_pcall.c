/*
 * lua_pcall.c - Phase 2a clustering attempt to locate lua_pcall.
 *
 * Background (Phase 0 report.md, gap #5):
 *   lua_pcall has no string anchor and the engine's bytecode path does
 *   not exhibit a clean (L, int, int, int) call site that resolves to a
 *   thin wrapper around lj_docall. The confirmed LuaJIT API cluster
 *   lives in 0xc74050..0xc7ef4a; once one LuaJIT API function is
 *   confirmed, the others are nearby.
 *
 * What we attempt here:
 *   1. Survey every .pdata entry in the cluster window (plus any leaf
 *      addresses already encountered in the init call graph).
 *   2. For each candidate body, score by the lua_pcall shape:
 *        - small body (< ~250 bytes; lua_pcall is a thin wrapper)
 *        - 4-arg (L, int, int, int) call-site shape, evidenced by the
 *          first instructions writing to rdx/r8/r9 (or moving them into
 *          stack slots) without dereferencing them as pointers
 *        - calls exactly one internal "docall" routine (size >> body)
 *   3. If a single high-scoring candidate emerges, record it with
 *      reasoning. Otherwise emit an honest deferral that names what was
 *      surveyed and why no candidate could be confirmed.
 *
 * An honest negative is the expected and correct outcome: Phase 0 could
 * not pin lua_pcall offline and we do not force-fit. Dynamic capture
 * (Phase 3) is the authoritative resolver.
 */
#include "engine_internal.h"
#include "util.h"
#include <capstone/capstone.h>

/* ---- Inputs from the confirmed Category B set --------------------- */
typedef struct {
    uint32_t rva;
    char     name[32];
} confirmed_api_t;

/* Gather the confirmed cluster addresses from result->cat_b. */
static int gather_confirmed(const dt_result_t *r, confirmed_api_t *out, int max) {
    int n = 0;
    for (int i = 0; i < r->cat_b_count; ++i) {
        const dt_cat_b_t *cb = &r->cat_b[i];
        if (dt_streq(cb->name, "lua_pcall")) continue;
        if (dt_streq(cb->confidence, "none")) continue;
        for (int k = 0; k < cb->candidate_rva_count; ++k) {
            if (n >= max) break;
            out[n].rva = (uint32_t)strtoull(cb->candidate_rvas[k], NULL, 0);
            dt_strncpy(out[n].name, cb->name, sizeof(out[n].name));
            n++;
        }
    }
    return n;
}

/* Score a candidate body for the lua_pcall shape.
 * Returns 1..100 (higher is better); 0 means "not a candidate". */
static int score_pcall(const dt_engine_ctx_t *ctx,
                       uint32_t rva, const dt_runtime_function_t *rf,
                       char *reasoning, size_t reasoning_sz) {
    cs_insn insns[256];
    int n = dt_disasm_range(rva, rf ? rf->end : 0, insns, 256);
    if (n <= 3) {
        if (reasoning) snprintf(reasoning, reasoning_sz,
            "body too small (%d insns)", n);
        return 0;
    }
    int body_size = rf ? (int)(rf->end - rva) : 0;

    /* Reject obvious misfits: way too large. */
    if (body_size > 0 && body_size > 400) {
        if (reasoning) snprintf(reasoning, reasoning_sz,
            "body too large (%dB) for a thin wrapper", body_size);
        return 0;
    }

    /* Find call targets inside the body. */
    uint32_t cts[16]; int n_cts = 0;
    int n_calls = 0;
    int n_indirect = 0;
    int saw_ret = 0;
    int writes_rdx = 0, writes_r8 = 0, writes_r9 = 0;
    for (int i = 0; i < n; ++i) {
        const cs_insn *ins = &insns[i];
        if (strcmp(ins->mnemonic, "call") == 0) {
            n_calls++;
            if (dt_starts_with(ins->op_str, "0x")) {
                if (n_cts < 16)
                    cts[n_cts++] = (uint32_t)strtoull(ins->op_str, NULL, 0);
            } else {
                n_indirect++;
            }
            continue;
        }
        if (strcmp(ins->mnemonic, "ret") == 0 || strcmp(ins->mnemonic, "retn") == 0) {
            saw_ret = 1;
        }
        /* Track writes to rdx/r8/r9 (the int args after L=rcx).
         * Lua_pcall(L, nargs, nresults, errfunc) sets these up. */
        {
            char buf[8];
            snprintf(buf, sizeof(buf), "rdx,");
            if (dt_starts_with(ins->op_str, buf) || dt_streq(ins->op_str, "rdx"))
                writes_rdx = 1;
            snprintf(buf, sizeof(buf), "r8,");
            if (dt_starts_with(ins->op_str, buf) || dt_streq(ins->op_str, "r8"))
                writes_r8 = 1;
            snprintf(buf, sizeof(buf), "r9,");
            if (dt_starts_with(ins->op_str, buf) || dt_streq(ins->op_str, "r9"))
                writes_r9 = 1;
        }
    }

    /* Shape requirements. */
    int score = 0;
    /* Small body or leaf: prefer. */
    if (body_size > 0 && body_size <= 200) score += 25;
    else if (body_size > 0 && body_size <= 300) score += 10;

    /* Has exactly one direct internal call (the docall). */
    if (n_cts == 1 && n_indirect == 0) {
        score += 30;
        /* Bonus if that callee is much bigger (docall is substantial). */
        const dt_runtime_function_t *crf = dt_find_runtime_function(ctx, cts[0]);
        if (crf && (crf->end - crf->begin) > 200) score += 15;
    } else if (n_cts >= 1 && n_cts <= 2) {
        score += 10;
    } else {
        if (reasoning) snprintf(reasoning, reasoning_sz,
            "%d direct calls + %d indirect \xe2\x80\x94 not thin-wrapper shape",
            n_cts, n_indirect);
        return 0;
    }

    /* 4-arg shape: should set up at least rdx and r8 (nargs, nresults). */
    int int_args = writes_rdx + writes_r8 + writes_r9;
    if (int_args >= 2) score += 20;
    if (int_args >= 3) score += 10;
    if (int_args == 0) {
        if (reasoning) snprintf(reasoning, reasoning_sz,
            "no integer-arg setup detected");
        return 0;
    }

    if (reasoning) {
        char cts_list[128] = "";
        int p = 0;
        for (int i = 0; i < n_cts; ++i) {
            char buf[24];
            int w = snprintf(buf, sizeof(buf), "0x%x%s", cts[i],
                             (i + 1 < n_cts) ? "," : "");
            if (p + w < (int)sizeof(cts_list)) {
                memcpy(cts_list + p, buf, w); p += w;
            }
        }
        snprintf(reasoning, reasoning_sz,
            "body_size=%dB, n_calls=%d (direct: %s), int_args_setup=%d/3 "
            "(rdx=%d r8=%d r9=%d); score=%d",
            body_size, n_calls, cts_list, int_args,
            writes_rdx, writes_r8, writes_r9, score);
    }
    return score;
}

void dt_phase_lua_pcall(const dt_engine_ctx_t *ctx, dt_result_t *r) {
    /* Locate the cat_b[lua_pcall] slot prepared by phase_c_classify. */
    dt_cat_b_t *pc = NULL;
    for (int i = 0; i < r->cat_b_count; ++i) {
        if (dt_streq(r->cat_b[i].name, "lua_pcall")) { pc = &r->cat_b[i]; break; }
    }
    if (!pc) return;

    /* Survey cluster: confirmed API range. */
    confirmed_api_t confirmed[8];
    int n_conf = gather_confirmed(r, confirmed, 8);
    if (n_conf == 0) {
        dt_strncpy(pc->confidence, "none", sizeof(pc->confidence));
        snprintf(pc->evidence, sizeof(pc->evidence),
            "lua_pcall clustering skipped: no confirmed LuaJIT API "
            "addresses to anchor the cluster survey.");
        snprintf(r->pcall_summary, sizeof(r->pcall_summary),
            "skipped (no anchor)");
        return;
    }

    uint32_t lo = confirmed[0].rva, hi = confirmed[0].rva;
    for (int i = 1; i < n_conf; ++i) {
        if (confirmed[i].rva < lo) lo = confirmed[i].rva;
        if (confirmed[i].rva > hi) hi = confirmed[i].rva;
    }
    /* Pad the window a bit on each side; lua_pcall could sit just outside. */
    uint32_t win_lo = (lo > 0x1000) ? lo - 0x1000 : 0;
    uint32_t win_hi = hi + 0x1000;

    /* Walk .pdata entries in the window, score each. */
    dt_pcall_candidate_t cands[64];
    int ncands = 0;
    const dt_runtime_function_t *arr = ctx->runtime_functions;
    for (uint32_t i = 0; i < ctx->runtime_function_count; ++i) {
        if (arr[i].begin < win_lo || arr[i].begin >= win_hi) continue;
        /* Skip the confirmed addresses themselves. */
        int skip = 0;
        for (int k = 0; k < n_conf; ++k) {
            if (arr[i].begin == confirmed[k].rva ||
                (arr[i].begin <= confirmed[k].rva &&
                 arr[i].end   >  confirmed[k].rva)) { skip = 1; break; }
        }
        if (skip) continue;

        char reasoning[256];
        int score = score_pcall(ctx, arr[i].begin, &arr[i],
                                reasoning, sizeof(reasoning));
        if (score <= 30) continue;
        if (ncands < 64) {
            cands[ncands].rva   = arr[i].begin;
            cands[ncands].score = score;
            dt_strncpy(cands[ncands].reasoning, reasoning,
                       sizeof(cands[ncands].reasoning));
            ncands++;
        }
    }

    /* Also probe leaf addresses already encountered in the call graph. */
    for (int i = 0; i < r->classified_count; ++i) {
        const dt_classified_t *c = &r->classified[i];
        if (c->has_pdata) continue;            /* already surveyed above */
        uint32_t rva = (uint32_t)strtoull(c->real_target_rva, NULL, 0);
        if (rva < win_lo || rva >= win_hi) continue;
        int skip = 0;
        for (int k = 0; k < n_conf; ++k)
            if (rva == confirmed[k].rva) { skip = 1; break; }
        if (skip) continue;
        int already = 0;
        for (int k = 0; k < ncands; ++k)
            if (cands[k].rva == rva) { already = 1; break; }
        if (already) continue;

        char reasoning[256];
        int score = score_pcall(ctx, rva, NULL, reasoning, sizeof(reasoning));
        if (score <= 30) continue;
        if (ncands < 64) {
            cands[ncands].rva   = rva;
            cands[ncands].score = score;
            dt_strncpy(cands[ncands].reasoning, reasoning,
                       sizeof(cands[ncands].reasoning));
            ncands++;
        }
    }

    /* Sort by score descending. */
    for (int i = 1; i < ncands; ++i) {
        dt_pcall_candidate_t v = cands[i];
        int j = i - 1;
        while (j >= 0 && cands[j].score < v.score) { cands[j + 1] = cands[j]; --j; }
        cands[j + 1] = v;
    }

    /* Publish the survey to the result (kept separate from cat_b for the
     * detailed reasoning). Cap at DT_MAX_LUA_PCALL_CANDS so we don't lie
     * about how many fit in the fixed-size result array. */
    int published = ncands;
    if (published > DT_MAX_LUA_PCALL_CANDS) published = DT_MAX_LUA_PCALL_CANDS;
    for (int i = 0; i < published; ++i)
        r->pcall_candidates[i] = cands[i];
    r->pcall_candidate_count = published;

    /* Decision rule:
     *   - clear winner with score >= 70 AND a 15+ point gap to #2  -> emit
     *   - otherwise                                                        -> defer
     *
     * Phase 0 could not pin lua_pcall; we expect to defer. The bar is
     * deliberately high to avoid false positives.
     */
    int emit = 0;
    if (ncands > 0 && cands[0].score >= 70 &&
        (ncands == 1 || cands[0].score - cands[1].score >= 15)) {
        emit = 1;
    }

    if (emit) {
        char rva_str[32];
        dt_hex(rva_str, sizeof(rva_str), cands[0].rva);
        dt_strncpy(pc->candidate_rvas[0], rva_str,
                   sizeof(pc->candidate_rvas[0]));
        pc->candidate_rva_count = 1;
        dt_strncpy(pc->confidence, "medium", sizeof(pc->confidence));
        snprintf(pc->evidence, sizeof(pc->evidence),
            "lua_pcall clustering candidate (score %d): %s. "
            "Confidence is medium only \xe2\x80\x94 dynamic confirmation "
            "(Phase 3 hook) is required before use.",
            cands[0].score, cands[0].reasoning);
        snprintf(r->pcall_summary, sizeof(r->pcall_summary),
            "candidate 0x%x (score %d, %d total surveyed)",
            cands[0].rva, cands[0].score, ncands);
    } else {
        pc->candidate_rva_count = 0;
        dt_strncpy(pc->confidence, "none", sizeof(pc->confidence));
        snprintf(pc->evidence, sizeof(pc->evidence),
            "lua_pcall: deferred to Phase 3 dynamic confirmation. "
            "Surveyed window [0x%x, 0x%x) around the confirmed LuaJIT "
            "API cluster (%d functions scored; %d passed the thin-wrapper "
            "shape filter, but none cleared the high-confidence bar \xe2\x80\x94 "
            "no unique winner with a clear margin). lua_pcall has no string "
            "anchor and the engine's bytecode path does not exhibit a clean "
            "(L,int,int,int) call site resolving to a thin lj_docall wrapper. "
            "Phase 0 also deferred this; dynamic capture in Story 3 is the "
            "authoritative resolver.",
            win_lo, win_hi, n_conf, ncands);
        snprintf(r->pcall_summary, sizeof(r->pcall_summary),
            "deferred (surveyed %d cands, top score %d)",
            ncands, ncands ? cands[0].score : 0);
    }
}
