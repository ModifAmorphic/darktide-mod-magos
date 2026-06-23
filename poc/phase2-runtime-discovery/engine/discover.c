/*
 * discover.c - Phase orchestration: A (anchor sanity) -> B (cat A xrefs) ->
 *              C (init selection + call classification + cat B) ->
 *              D (error-string negative result) -> lua_pcall clustering ->
 *              methodology gaps -> result assembly.
 *
 * Pure translation of discover.py's main() / phase_a..phase_d. The
 * top-level entry dt_discover() is in this file.
 */
#include "engine_internal.h"
#include "util.h"

/* ------------------------------------------------------------------- */
/* Phase A: anchor sanity check.                                       */
/* ------------------------------------------------------------------- */
void dt_phase_a(const dt_engine_ctx_t *ctx, dt_result_t *r) {
    uint32_t delta = ctx->rdata->rva - ctx->rdata->file_offset;
    r->rdata_delta = delta;
    r->rdata_delta_matches_doc = (delta == DT_EXPECTED_RDATA_DELTA);

    int count = 0;
    int n_corrections = 0;
    for (int i = 0; i < dt_anchors_s3_count; ++i) {
        const dt_anchor_def_t *a = &dt_anchors_s3[i];
        dt_anchor_sanity_t *s = &r->anchor_sanity[count++];
        memset(s, 0, sizeof(*s));
        dt_strncpy(s->label, a->label, sizeof(s->label));
        dt_strncpy(s->string, a->expected, sizeof(s->string));
        dt_hex(s->documented_file_offset, sizeof(s->documented_file_offset),
               a->documented_file_offset);

        /* For the offline tool, the image buffer is laid out by RVA, so
         * the documented FILE OFFSET must be converted to RVA via the
         * .rdata delta (same offset math as discover.py). */
        uint32_t anchor_rva = a->documented_file_offset + delta;
        dt_hex(s->computed_rva, sizeof(s->computed_rva), anchor_rva);

        /* Read expected bytes. In Phase 0's Python this is raw[file_off],
         * but our engine consumes an RVA-laid-out image, so the byte that
         * was at file_off in the file now lives at anchor_rva in image.
         * Reading at anchor_rva is exactly what the runtime (Phase 2b)
         * will do too: only RVAs are meaningful in a mapped module. */
        const uint8_t *p = dt_image_at(ctx, anchor_rva,
                                       strlen(a->expected));
        int match = 0;
        if (p && memcmp(p, a->expected, strlen(a->expected)) == 0) match = 1;
        dt_strncpy(s->actual_at_offset, match ? "yes" : "no",
                   sizeof(s->actual_at_offset));
        if (!match) {
            /* Capture actual bytes hex for the report. */
            const uint8_t *q = p;
            int p2 = 0;
            for (int k = 0; q && k < (int)strlen(a->expected) && p2 < 60; ++k)
                p2 += snprintf(s->actual_bytes_hex + p2,
                               sizeof(s->actual_bytes_hex) - p2,
                               "%02x", q[k]);
            snprintf(s->doc_correction, sizeof(s->doc_correction),
                     "String at 0x%x does not match documented '%s'",
                     a->documented_file_offset, a->expected);
            s->has_correction = 1;
            if (n_corrections < DT_MAX_DOC_CORRECTIONS) {
                dt_strncpy(r->doc_corrections[n_corrections++],
                           s->doc_correction,
                           sizeof(r->doc_corrections[0]));
            }
        }
    }
    r->anchor_sanity_count = count;
    r->doc_correction_count = n_corrections;
}

/* ------------------------------------------------------------------- */
/* Phase B: per-anchor xref -> containing functions (Category A).      */
/* ------------------------------------------------------------------- */
void dt_phase_b(const dt_engine_ctx_t *ctx, dt_result_t *r) {
    int count = 0;
    for (int i = 0; i < r->anchor_sanity_count; ++i) {
        const dt_anchor_sanity_t *sane = &r->anchor_sanity[i];
        if (count >= DT_MAX_CAT_A) break;
        dt_cat_a_t *ca = &r->cat_a[count++];
        memset(ca, 0, sizeof(*ca));
        dt_strncpy(ca->anchor, sane->label, sizeof(ca->anchor));
        dt_strncpy(ca->anchor_string, sane->string, sizeof(ca->anchor_string));
        dt_strncpy(ca->anchor_rva, sane->computed_rva, sizeof(ca->anchor_rva));

        uint32_t target_rva = (uint32_t)strtoull(sane->computed_rva, NULL, 0);
        uint32_t sites[DT_MAX_XREF_SITES];
        int n_sites = 0;
        dt_find_lea_xrefs(ctx, target_rva, sites, &n_sites, DT_MAX_XREF_SITES);

        ca->xref_count = n_sites;
        for (int k = 0; k < n_sites && k < DT_MAX_XREF_SITES; ++k)
            dt_hex(ca->xref_sites[k], sizeof(ca->xref_sites[k]), sites[k]);

        /* Map each site to its containing function (dedup by begin). */
        for (int k = 0; k < n_sites; ++k) {
            const dt_runtime_function_t *rf = dt_find_runtime_function(ctx, sites[k]);
            if (!rf) continue;
            int dup = 0;
            for (int m = 0; m < ca->containing_count; ++m) {
                if (strtoull(ca->containing[m].begin_rva, NULL, 0) == rf->begin) {
                    dup = 1; break;
                }
            }
            if (dup) continue;
            if (ca->containing_count >= DT_MAX_CONTAINING_FUNCS) continue;
            dt_func_range_t *fr = &ca->containing[ca->containing_count++];
            dt_hex(fr->begin_rva, sizeof(fr->begin_rva), rf->begin);
            dt_hex(fr->end_rva,   sizeof(fr->end_rva),   rf->end);
            fr->size_bytes = (int)(rf->end - rf->begin);
        }
    }
    r->cat_a_count = count;
}

/* ------------------------------------------------------------------- */
/* Phase C classify: enumerate init's calls, group, classify, build B.*/
/* ------------------------------------------------------------------- */
void dt_phase_c_classify(const dt_engine_ctx_t *ctx, dt_result_t *r) {
    /* Enumerate the call graph of the init candidate. */
    int ncg = dt_enumerate_calls(r->init.begin, r->init.end,
                                 r->call_graph, DT_MAX_CALL_GRAPH);
    r->call_graph_count = ncg;

    /* Group by real_target_rva. */
    typedef struct {
        char key[32];
        int  cg_idx_first;             /* index into r->call_graph */
        int  cg_indices[DT_MAX_XREF_SITES];
        int  n_cg;
    } group_t;
    group_t groups[DT_MAX_CLASSIFIED];
    int n_groups = 0;

    for (int i = 0; i < ncg; ++i) {
        const dt_call_edge_t *e = &r->call_graph[i];
        if (!dt_streq(e->kind, "direct")) continue;
        int slot = -1;
        for (int j = 0; j < n_groups; ++j)
            if (dt_streq(groups[j].key, e->real_target_rva)) { slot = j; break; }
        if (slot < 0) {
            if (n_groups >= DT_MAX_CLASSIFIED) continue;
            slot = n_groups++;
            memset(&groups[slot], 0, sizeof(groups[slot]));
            dt_strncpy(groups[slot].key, e->real_target_rva,
                       sizeof(groups[slot].key));
            groups[slot].cg_idx_first = i;
        }
        if (groups[slot].n_cg < DT_MAX_XREF_SITES)
            groups[slot].cg_indices[groups[slot].n_cg++] = i;
    }

    /* For each group, classify and build a classified entry. */
    int nc = 0;
    for (int g = 0; g < n_groups; ++g) {
        if (nc >= DT_MAX_CLASSIFIED) break;
        const dt_call_edge_t *first = &r->call_graph[groups[g].cg_idx_first];
        dt_classified_t *c = &r->classified[nc++];
        memset(c, 0, sizeof(*c));
        dt_strncpy(c->target_rva, first->target_rva, sizeof(c->target_rva));
        dt_strncpy(c->real_target_rva, first->real_target_rva,
                   sizeof(c->real_target_rva));
        c->is_thunk = first->is_thunk;
        c->thunk_chain_len = first->thunk_chain_len;
        for (int i = 0; i < first->thunk_chain_len; ++i)
            dt_strncpy(c->thunk_chain[i], first->thunk_chain[i],
                       sizeof(c->thunk_chain[i]));
        c->has_pdata = first->has_pdata;
        dt_strncpy(c->func_begin, first->func_begin, sizeof(c->func_begin));
        dt_strncpy(c->func_end,   first->func_end,   sizeof(c->func_end));
        c->func_size = first->func_size;
        if (first->has_import) {
            dt_strncpy(c->import_name, first->import_name,
                       sizeof(c->import_name));
            c->has_import = 1;
        }
        for (int i = 0; i < groups[g].n_cg; ++i) {
            const dt_call_edge_t *e = &r->call_graph[groups[g].cg_indices[i]];
            if (c->call_site_count < DT_MAX_XREF_SITES) {
                dt_strncpy(c->call_sites[c->call_site_count++],
                           e->call_site_rva, sizeof(c->call_sites[0]));
            }
        }
        dt_strncpy(c->arg_rcx, first->arg_rcx, sizeof(c->arg_rcx));
        dt_strncpy(c->arg_rdx, first->arg_rdx, sizeof(c->arg_rdx));
        dt_strncpy(c->arg_r8,  first->arg_r8,  sizeof(c->arg_r8));
        dt_strncpy(c->arg_r9,  first->arg_r9,  sizeof(c->arg_r9));

        /* Classify. */
        if (c->has_import) {
            dt_strncpy(c->classification, "import", sizeof(c->classification));
            dt_strncpy(c->confidence,     "high",   sizeof(c->confidence));
            snprintf(c->evidence, sizeof(c->evidence),
                     "import thunk -> %s", c->import_name);
        } else {
            uint32_t real = (uint32_t)strtoull(c->real_target_rva, NULL, 0);
            const dt_runtime_function_t *rf = dt_find_runtime_function(ctx, real);
            /* Use the same internal classify_by_body. It's static in
             * classify.c; expose a thin wrapper. We replicate the body
             * shape matchers here by calling the wrapper. */
            extern void dt_classify_by_body_wrap(uint32_t real,
                const dt_runtime_function_t *rf,
                char *cls, size_t cls_sz,
                char *conf, size_t conf_sz,
                char *ev, size_t ev_sz,
                char *internal_lua_load, size_t ill_sz);
            dt_classify_by_body_wrap(real, rf,
                c->classification, sizeof(c->classification),
                c->confidence,     sizeof(c->confidence),
                c->evidence,       sizeof(c->evidence),
                c->internal_lua_load_rva, sizeof(c->internal_lua_load_rva));
        }
    }
    r->classified_count = nc;

    /* Build category B candidates. */
    int cb = 0;
    /* lua_newstate (backward dataflow). */
    if (cb < DT_MAX_CAT_B) {
        dt_identify_lua_newstate(ctx, r, r->classified, r->classified_count,
                                 &r->cat_b[cb++]);
    }
    /* lua_atpanic. */
    for (int i = 0; i < nc; ++i) {
        if (dt_streq(r->classified[i].classification, "lua_atpanic")) {
            if (cb < DT_MAX_CAT_B)
                dt_classified_to_cat_b(&r->classified[i], "lua_atpanic",
                                       "direct-call-trace", &r->cat_b[cb++]);
            break;
        }
    }
    /* lua_gettop. */
    for (int i = 0; i < nc; ++i) {
        if (dt_streq(r->classified[i].classification, "lua_gettop")) {
            if (cb < DT_MAX_CAT_B)
                dt_classified_to_cat_b(&r->classified[i], "lua_gettop",
                                       "direct-call-trace", &r->cat_b[cb++]);
            break;
        }
    }
    /* luaL_loadbuffer (from bytecode). */
    if (cb < DT_MAX_CAT_B)
        dt_find_loadbuffer_from_bytecode(ctx, r, &r->cat_b[cb++]);

    /* lua_pcall placeholder; the dedicated clustering phase fills in
     * either a real candidate or an honest deferral. */
    if (cb < DT_MAX_CAT_B) {
        dt_cat_b_t *pc = &r->cat_b[cb++];
        memset(pc, 0, sizeof(*pc));
        dt_strncpy(pc->name, "lua_pcall", sizeof(pc->name));
        dt_strncpy(pc->discovery_method, "direct-call-trace",
                   sizeof(pc->discovery_method));
        /* (evidence/confidence filled by dt_phase_lua_pcall) */
    }
    r->cat_b_count = cb;
}

/* ------------------------------------------------------------------- */
/* Phase D: error-string cross-check (documents the negative result).  */
/* ------------------------------------------------------------------- */
void dt_phase_d(const dt_engine_ctx_t *ctx, dt_result_t *r) {
    uint32_t delta = ctx->rdata->rva - ctx->rdata->file_offset;
    dt_phase_d_t *pd = &r->phase_d;
    pd->any_xref_found = 0;
    pd->result_count = 0;

    for (int i = 0; i < dt_error_strings_s5_count; ++i) {
        const dt_anchor_def_t *a = &dt_error_strings_s5[i];
        dt_err_string_result_t *res = &pd->results[pd->result_count++];
        memset(res, 0, sizeof(*res));
        dt_strncpy(res->label,  a->label,   sizeof(res->label));
        dt_strncpy(res->string, a->expected, sizeof(res->string));
        dt_hex(res->documented_file_offset, sizeof(res->documented_file_offset),
               a->documented_file_offset);

        uint32_t anchor_rva = a->documented_file_offset + delta;
        dt_hex(res->computed_rva, sizeof(res->computed_rva), anchor_rva);

        /* String still at the documented offset? Same RVA-vs-file-offset
         * note as phase_a: read at anchor_rva (the image is RVA-mapped). */
        const uint8_t *p = dt_image_at(ctx, anchor_rva,
                                       strlen(a->expected));
        res->string_at_offset =
            (p && memcmp(p, a->expected, strlen(a->expected)) == 0);

        /* LEA-xref scan. */
        uint32_t sites[DT_MAX_XREF_SITES];
        int n_sites = 0;
        dt_find_lea_xrefs(ctx, anchor_rva, sites, &n_sites, DT_MAX_XREF_SITES);
        res->lea_xref_count = n_sites;
        if (n_sites > 0) pd->any_xref_found = 1;

        /* Pointer-table hits: count occurrences of (image_base + anchor_rva)
         * as a 64-bit little-endian value anywhere in the image. */
        uint64_t va = ctx->image_base + anchor_rva;
        uint8_t needle[8];
        for (int b = 0; b < 8; ++b) needle[b] = (uint8_t)(va >> (8 * b));
        /* Linear scan; ~18MB / 8 = ~2.3M iterations, fine. */
        int hits = 0;
        size_t img = ctx->image_size;
        const uint8_t *base = ctx->image;
        for (size_t k = 0; k + 8 <= img; ++k) {
            if (base[k] == needle[0] &&
                memcmp(base + k, needle, 8) == 0) hits++;
        }
        res->pointer_table_hits = hits;
    }

    dt_strncpy(pd->methodology_note,
        "Phase D as documented does NOT work: 0 LEA xrefs and 0 pointer-"
        "table hits for every \xc2\xa7" "5 error string. The strings are "
        "entries in a contiguous lj_err_msg[] block in .rdata and are "
        "interned into LuaJIT's string hash table at VM init via "
        "lj_str_new(); the code thereafter references them by GCstr* "
        "handle, never by raw .rdata address. Phase 2 must find the "
        "lj_str_new interning loop or use a different anchor (e.g. a "
        "known LuaJIT function body signature), not LEA-xref on "
        "individual error strings.",
        sizeof(pd->methodology_note));
}

/* ------------------------------------------------------------------- */
/* Methodology gaps (6 entries, transcribed from discover.py).         */
/* ------------------------------------------------------------------- */
void dt_phase_methodology_gaps(dt_result_t *r) {
    int n = 0;
    dt_strncpy(r->methodology_gaps[n++],
        "CFG/hot-patch thunks (5-byte E9 rel32 + cc padding) have NO "
        ".pdata entry of their own \xe2\x80\x94 they sit in gaps between "
        "RUNTIME_FUNCTION entries. Discovery MUST follow the thunk to the "
        "real body. Observed: lua_newstate is invoked at the thunk "
        "0xc7c000 whose real body is at 0xc7eea0.",
        sizeof(r->methodology_gaps[0]));
    dt_strncpy(r->methodology_gaps[n++],
        "Leaf functions (no prologue, SP unchanged, ret-only epilogue) "
        "are ALSO missing .pdata entries \xe2\x80\x94 MSVC omits them. "
        "lua_gettop (0xc74050), lua_atpanic (0xc77f40), and the lua_push* "
        "primitives (e.g. 0xc74770) all fall in .pdata gaps. .pdata is "
        "NOT a complete function map; Phase 2 must handle addresses "
        "outside .pdata by disasming bytes directly and trimming at the "
        "first ret.",
        sizeof(r->methodology_gaps[0]));
    dt_strncpy(r->methodology_gaps[n++],
        "Import thunks (FF 25 disp32 = jmp [rip+disp32]) are a third "
        "category of call target in .pdata gaps. They must be resolved "
        "through the IAT, not treated as function bodies. Example: "
        "0xdf593c -> VCRUNTIME140.dll!memmove.",
        sizeof(r->methodology_gaps[0]));
    dt_strncpy(r->methodology_gaps[n++],
        "Phase D as documented does not work. The \xc2\xa7" "5 LuaJIT error "
        "strings (attempt_to_call, bad_argument, loop_in_gettable, "
        "invalid_key_next) have ZERO LEA xrefs and ZERO pointer-table "
        "references anywhere in the binary. They are entries in a "
        "contiguous lj_err_msg[] block and are interned via lj_str_new() "
        "at VM init; thereafter referenced by GCstr* handle. Phase 2 must "
        "use a different LuaJIT-internal anchor (e.g. the lj_str_new "
        "interning loop, or a known function-body signature). Do NOT "
        "LEA-xref individual error strings.",
        sizeof(r->methodology_gaps[0]));
    dt_strncpy(r->methodology_gaps[n++],
        "lua_pcall could not be conclusively identified offline. It has "
        "no string anchor and the engine's bytecode path does not exhibit "
        "a clean (L,int,int,int) call site resolving to a thin lj_docall "
        "wrapper. Phase 2 must locate it by clustering near the other "
        "confirmed LuaJIT API functions in the 0xc7xxxx region or by "
        "structural pattern.",
        sizeof(r->methodology_gaps[0]));
    dt_strncpy(r->methodology_gaps[n++],
        "The lua_panic string anchor's containing function is lua_panic "
        "ITSELF (it logs its own name), NOT the init code. The reliable "
        "path to init is: find the lua_panic body, then find LEA "
        "references to that body's address (the &lua_panic taken for "
        "lua_atpanic). The anchors doc \xc2\xa7" "7 implies the string xref "
        "lands directly in init code; it does not.",
        sizeof(r->methodology_gaps[0]));
    r->methodology_gap_count = n;
}

/* ------------------------------------------------------------------- */
/* dt_discover — top-level entry.                                      */
/* ------------------------------------------------------------------- */
/*
 * The engine reads PE structure from the in-memory image directly:
 *   - DOS header (e_lfanew) at image[0x3C]
 *   - PE/optional headers via packed-struct member access (pe.c)
 *   - Section headers from the PE section table, including each
 *     section's PointerToRawData field
 *
 * This works against BOTH the offline-tool's RVA-mapped buffer AND a
 * live in-process module (Phase 2b), because the Windows loader
 * preserves the PE headers (DOS/PE/optional/section tables) VERBATIM
 * when it maps a module: only section *contents* are relocated to
 * their VirtualAddress, and the IAT is fixed up in place. The section
 * headers' PointerToRawData field still points at a file offset (which
 * is meaningless for a mapped module), but the engine only reads
 * VirtualAddress, VirtualSize, and SizeOfRawData from section headers
 * — never PointerToRawData. (The offline tool's own RVA-mapping step
 * in tool/discover.c DOES use PointerToRawData, but that's the
 * wrapper, not the engine; the engine is fed an already-RVA-laid-out
 * buffer.) See pe.c find_section_by_name() for the fields consumed.
 */
int dt_discover(const uint8_t *image, size_t image_size, uint64_t image_base,
                dt_result_t *result) {
    memset(result, 0, sizeof(*result));

    const dt_section_t *text, *rdata, *pdata;
    const dt_runtime_function_t *rf;
    uint32_t rf_count;
    int rc = dt_engine_setup(image, image_size, image_base,
                             &text, &rdata, &pdata, &rf, &rf_count);
    if (rc != 0) return rc;

    const dt_engine_ctx_t *ctx = dt_engine_ctx_active();

    /* Carry sections into the result struct for output JSON. */
    result->image_base              = ctx->image_base;
    result->text                    = *text;
    result->rdata                   = *rdata;
    result->pdata                   = *pdata;
    result->runtime_function_count  = rf_count;

    dt_phase_a(ctx, result);
    dt_phase_b(ctx, result);
    dt_phase_c_select_init(ctx, result);
    dt_phase_c_classify(ctx, result);
    dt_phase_d(ctx, result);
    dt_phase_lua_pcall(ctx, result);
    dt_phase_methodology_gaps(result);
    return 0;
}
