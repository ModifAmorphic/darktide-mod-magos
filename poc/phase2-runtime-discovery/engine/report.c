/*
 * report.c - Human-readable markdown report for the Phase 2a engine.
 *
 * Mirrors the structure of poc/phase0-offline-discovery/report.md
 * so the two reports can be diffed side-by-side. Generated from the same
 * dt_result_t that drives JSON output.
 */
#include "engine_internal.h"
#include "util.h"
#include <stdio.h>

int dt_write_report(const dt_result_t *r, FILE *f) {
    char buf[32];

    fprintf(f, "# Phase 2a Runtime Discovery Engine \xe2\x80\x94 Report\n\n");
    fprintf(f, "> Pure offline binary analysis of `Darktide.exe`, produced by\n");
    fprintf(f, "> the C port of the Phase 0 `discover.py`. No DLL, no game\n");
    fprintf(f, "> launch, no injection. This report mirrors Phase 0's report.md\n");
    fprintf(f, "> so the two can be diffed field-by-field.\n\n");
    fprintf(f, "- **Binary:** `%s`\n", r->binary_path);
    fprintf(f, "- **Size:** %u bytes (expected %u)\n",
            r->binary_size, DT_EXPECTED_FILE_SIZE);
    fprintf(f, "- **SHA-256:** `%s`\n\n", r->sha256);

    /* ---- PE summary ---- */
    fprintf(f, "## PE parse summary\n\n");
    fprintf(f, "| Section | RVA | File offset | Raw size | Virtual size |\n");
    fprintf(f, "|---------|-----|-------------|----------|--------------|\n");
    fprintf(f, "| `.text`  | `0x%x` | `0x%x` | %u | %u |\n",
            r->text.rva, r->text.file_offset, r->text.raw_size, r->text.virtual_size);
    fprintf(f, "| `.rdata` | `0x%x` | `0x%x` | %u | %u |\n",
            r->rdata.rva, r->rdata.file_offset, r->rdata.raw_size, r->rdata.virtual_size);
    fprintf(f, "| `.pdata` | `0x%x` | `0x%x` | %u | %u |\n\n",
            r->pdata.rva, r->pdata.file_offset, r->pdata.raw_size, r->pdata.virtual_size);
    fprintf(f, "- **.rdata RVA delta:** `0x%x` (%u) \xe2\x80\x94 **%s** the documented `0x%x`.\n",
            r->rdata_delta, r->rdata_delta,
            r->rdata_delta_matches_doc ? "matches" : "DOES NOT MATCH",
            DT_EXPECTED_RDATA_DELTA);
    fprintf(f, "- **.pdata entries:** %u non-zero RUNTIME_FUNCTION records.\n\n",
            r->runtime_function_count);

    /* ---- Anchor sanity ---- */
    fprintf(f, "## Anchor sanity check (Phase A)\n\n");
    fprintf(f, "Every \xc2\xa7" "3 Quick-Reference anchor was read at its documented file offset\n");
    fprintf(f, "and compared against the documented string.\n\n");
    fprintf(f, "| Anchor | File offset | At offset? | Computed RVA | Correction |\n");
    fprintf(f, "|--------|-------------|------------|--------------|------------|\n");
    for (int i = 0; i < r->anchor_sanity_count; ++i) {
        const dt_anchor_sanity_t *s = &r->anchor_sanity[i];
        fprintf(f, "| `%s` | `%s` | **%s** | `%s` | %s |\n",
                s->label, s->documented_file_offset, s->actual_at_offset,
                s->computed_rva, s->has_correction ? s->doc_correction : "\xe2\x80\x94");
    }
    int n_pass = 0;
    for (int i = 0; i < r->anchor_sanity_count; ++i)
        if (dt_streq(r->anchor_sanity[i].actual_at_offset, "yes")) n_pass++;
    fprintf(f, "\n**Result: %d/%d anchors verified at their documented offsets.**\n\n",
            n_pass, r->anchor_sanity_count);
    if (r->doc_correction_count == 0) {
        fprintf(f, "**No string-offset corrections needed.** Every \xc2\xa7" "3 anchor matched.\n\n");
    } else {
        fprintf(f, "**Corrections:**\n\n");
        for (int i = 0; i < r->doc_correction_count; ++i)
            fprintf(f, "- %s\n", r->doc_corrections[i]);
        fprintf(f, "\n");
    }

    /* ---- Category A ---- */
    fprintf(f, "## Category A engine functions (Phase B)\n\n");
    fprintf(f, "For each anchor: `.text` was scanned for RIP-relative LEA references;\n");
    fprintf(f, "each xref site was mapped to its containing RUNTIME_FUNCTION via .pdata.\n\n");
    fprintf(f, "| Anchor | Anchor RVA | Xrefs | Distinct containing funcs | Func RVAs |\n");
    fprintf(f, "|--------|-----------:|------:|--------------------------:|----------|\n");
    for (int i = 0; i < r->cat_a_count; ++i) {
        const dt_cat_a_t *a = &r->cat_a[i];
        char funcs[256] = "";
        for (int k = 0; k < a->containing_count; ++k) {
            char one[40];
            snprintf(one, sizeof(one), "%s`%s`", k ? ", " : "",
                     a->containing[k].begin_rva);
            strncat(funcs, one, sizeof(funcs) - strlen(funcs) - 1);
        }
        if (funcs[0] == '\0') dt_strncpy(funcs, "\xe2\x80\x94", sizeof(funcs));
        fprintf(f, "| `%s` | `%s` | %d | %d | %s |\n",
                a->anchor, a->anchor_rva, a->xref_count,
                a->containing_count, funcs);
    }
    fprintf(f, "\n");

    /* ---- Init candidate ---- */
    fprintf(f, "## Init candidate selection (Phase C)\n\n");
    if (!r->init.found) {
        fprintf(f, "**No init candidate could be selected.** Blocking result.\n\n");
    } else {
        fprintf(f, "Selected **`%s`\xe2\x80\x93`%s`** (%d bytes) as the LuaEnvironment init function.\n\n",
                r->init.begin_rva, r->init.end_rva, r->init.size_bytes);
        fprintf(f, "%s\n\n", r->init.reasoning);
        fprintf(f, "- `lua_environment` string marker present in body: **%s**\n",
                r->init.lua_environment_marker_found ? "True" : "False");
        dt_hex(buf, sizeof(buf), r->init.lua_panic_body_rva);
        fprintf(f, "- lua_panic body (string-xref owner): `%s`\n", buf);
        fprintf(f, "- LEA-of-&lua_panic sites (lua_atpanic setup): [");
        for (int i = 0; i < r->init.lea_of_lua_panic_count; ++i)
            fprintf(f, "%s'%s'", i ? ", " : "", r->init.lea_of_lua_panic_sites[i]);
        fprintf(f, "]\n\n");
    }

    /* ---- Category B ---- */
    fprintf(f, "## Category B LuaJIT candidates (Phase C)\n\n");
    fprintf(f, "Direct-call graph of the init candidate was enumerated; each distinct\n");
    fprintf(f, "call target was resolved (thunks followed, import thunks identified)\n");
    fprintf(f, "and classified by body shape and call context.\n\n");
    fprintf(f, "### Confirmed identifications\n\n");
    fprintf(f, "| Function | Candidate RVA(s) | Confidence | Discovery method |\n");
    fprintf(f, "|----------|------------------|------------|------------------|\n");
    for (int i = 0; i < r->cat_b_count; ++i) {
        const dt_cat_b_t *cb = &r->cat_b[i];
        char rvas[128] = "";
        for (int k = 0; k < cb->candidate_rva_count; ++k) {
            char one[24];
            snprintf(one, sizeof(one), "%s`%s`", k ? ", " : "",
                     cb->candidate_rvas[k]);
            strncat(rvas, one, sizeof(rvas) - strlen(rvas) - 1);
        }
        if (rvas[0] == '\0') dt_strncpy(rvas, "\xe2\x80\x94", sizeof(rvas));
        fprintf(f, "| `%s` | %s | **%s** | %s |\n",
                cb->name, rvas, cb->confidence, cb->discovery_method);
    }
    fprintf(f, "\n");
    for (int i = 0; i < r->cat_b_count; ++i) {
        const dt_cat_b_t *cb = &r->cat_b[i];
        fprintf(f, "### `%s` \xe2\x80\x94 confidence: %s\n\n", cb->name, cb->confidence);
        char rvas[256] = "";
        for (int k = 0; k < cb->candidate_rva_count; ++k) {
            char one[24];
            snprintf(one, sizeof(one), "%s`%s`", k ? ", " : "",
                     cb->candidate_rvas[k]);
            strncat(rvas, one, sizeof(rvas) - strlen(rvas) - 1);
        }
        if (rvas[0] == '\0') dt_strncpy(rvas, "none", sizeof(rvas));
        fprintf(f, "- **Candidate RVA(s):** %s\n", rvas);
        fprintf(f, "- **Discovery method:** %s\n", cb->discovery_method);
        fprintf(f, "- **Evidence:** %s\n\n", cb->evidence);
    }

    /* ---- Classified call graph ---- */
    fprintf(f, "### Full init-candidate call graph (classified)\n\n");
    fprintf(f, "| Call target | Thunk? | Real body | Size | Import | Classification | Confidence |\n");
    fprintf(f, "|-------------|--------|-----------|-----:|--------|----------------|------------|\n");
    /* Sort the table by real_target for readability. */
    int order[DT_MAX_CLASSIFIED];
    for (int i = 0; i < r->classified_count; ++i) order[i] = i;
    for (int i = 1; i < r->classified_count; ++i) {
        int v = order[i]; int j = i - 1;
        while (j >= 0 && strtoull(r->classified[order[j]].real_target_rva, NULL, 0) >
                          strtoull(r->classified[v].real_target_rva, NULL, 0)) {
            order[j + 1] = order[j]; --j;
        }
        order[j + 1] = v;
    }
    for (int idx = 0; idx < r->classified_count; ++idx) {
        const dt_classified_t *c = &r->classified[order[idx]];
        char size_buf[16];
        if (c->has_pdata) snprintf(size_buf, sizeof(size_buf), "%d", c->func_size);
        else              dt_strncpy(size_buf, "?", sizeof(size_buf));
        fprintf(f, "| `%s` | %s | `%s` | %s | %s | `%s` | %s |\n",
                c->target_rva, c->is_thunk ? "yes" : "no",
                c->real_target_rva, size_buf,
                c->has_import ? c->import_name : "\xe2\x80\x94",
                c->classification, c->confidence);
    }
    int n_indirect = 0;
    for (int i = 0; i < r->call_graph_count; ++i)
        if (dt_streq(r->call_graph[i].kind, "indirect")) n_indirect++;
    fprintf(f, "\nIndirect calls inside init (flagged for Phase 2 backward tracing): **%d**\n",
            n_indirect);
    if (n_indirect == 0) fprintf(f, "(none \xe2\x80\x94 the init path uses only direct calls.)\n");
    fprintf(f, "\n");

    /* ---- Phase D ---- */
    fprintf(f, "## LuaJIT error-string cross-check (Phase D)\n\n");
    fprintf(f, "The documented Phase D approach is to LEA-xref the \xc2\xa7" "5 LuaJIT error\n");
    fprintf(f, "strings. **This does not work.** All four documented error strings\n");
    fprintf(f, "produce zero LEA xrefs and zero pointer-table references anywhere in\n");
    fprintf(f, "the binary.\n\n");
    fprintf(f, "| Error string | File offset | String present? | LEA xrefs | Pointer hits |\n");
    fprintf(f, "|--------------|-------------|-----------------|----------:|-------------:|\n");
    for (int i = 0; i < r->phase_d.result_count; ++i) {
        const dt_err_string_result_t *e = &r->phase_d.results[i];
        fprintf(f, "| `%s` | `%s` | %s | %d | %d |\n",
                e->label, e->documented_file_offset,
                e->string_at_offset ? "True" : "False",
                e->lea_xref_count, e->pointer_table_hits);
    }
    fprintf(f, "\n**Methodology note:** %s\n\n", r->phase_d.methodology_note);

    /* ---- lua_pcall clustering ---- */
    fprintf(f, "## lua_pcall clustering attempt\n\n");
    fprintf(f, "Clustering summary: %s\n\n", r->pcall_summary);
    if (r->pcall_candidate_count > 0) {
        fprintf(f, "| RVA | Score | Reasoning |\n");
        fprintf(f, "|-----|------:|-----------|\n");
        for (int i = 0; i < r->pcall_candidate_count; ++i) {
            fprintf(f, "| `0x%x` | %d | %s |\n",
                    r->pcall_candidates[i].rva,
                    r->pcall_candidates[i].score,
                    r->pcall_candidates[i].reasoning);
        }
        fprintf(f, "\n");
    }

    /* ---- Methodology gaps ---- */
    fprintf(f, "## Methodology gaps (Phase 0 findings, all handled by this engine)\n\n");
    for (int i = 0; i < r->methodology_gap_count; ++i)
        fprintf(f, "%d. %s\n\n", i + 1, r->methodology_gaps[i]);

    return 0;
}
