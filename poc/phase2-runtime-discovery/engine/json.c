/*
 * json.c - JSON serializer for dt_result_t.
 *
 * Mirrors the schema produced by discover.py's json.dump(addresses,
 * indent=2, sort_keys=False). Non-ASCII chars are emitted as \uXXXX
 * to match Python's default ensure_ascii=True behaviour.
 */
#include "engine_internal.h"
#include "util.h"

/* JSON-escape one string into f. */
static void json_str(FILE *f, const char *s) {
    fputc('"', f);
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p) {
        unsigned char c = *p;
        switch (c) {
            case '"':  fputs("\\\"", f); break;
            case '\\': fputs("\\\\", f); break;
            case '\b': fputs("\\b", f);  break;
            case '\f': fputs("\\f", f);  break;
            case '\n': fputs("\\n", f);  break;
            case '\r': fputs("\\r", f);  break;
            case '\t': fputs("\\t", f);  break;
            default:
                if (c < 0x20) {
                    fprintf(f, "\\u%04x", c);
                } else if (c < 0x80) {
                    fputc(c, f);
                } else {
                    /* Decode UTF-8 multibyte -> single code point, emit as
                     * \uXXXX (or surrogate pair for code points > 0xFFFF). */
                    uint32_t cp = 0;
                    int extra = 0;
                    if ((c & 0xE0) == 0xC0)      { cp = c & 0x1F; extra = 1; }
                    else if ((c & 0xF0) == 0xE0) { cp = c & 0x0F; extra = 2; }
                    else if ((c & 0xF8) == 0xF0) { cp = c & 0x07; extra = 3; }
                    else { cp = c; extra = 0; }   /* invalid, pass through */
                    int ok = 1;
                    for (int k = 0; k < extra; ++k) {
                        unsigned char b = *(p + 1);
                        if ((b & 0xC0) != 0x80) { ok = 0; break; }
                        cp = (cp << 6) | (b & 0x3F);
                        p++;
                    }
                    if (!ok) {
                        fprintf(f, "\\u%04x", c);
                        continue;
                    }
                    if (cp <= 0xFFFF) {
                        fprintf(f, "\\u%04x", cp);
                    } else {
                        /* Surrogate pair. */
                        cp -= 0x10000;
                        uint32_t hi = 0xD800 + (cp >> 10);
                        uint32_t lo = 0xDC00 + (cp & 0x3FF);
                        fprintf(f, "\\u%04x\\u%04x", hi, lo);
                    }
                }
                break;
        }
    }
    fputc('"', f);
}

/* Emit a hex-string-valued field. */
static void json_kv_str(FILE *f, const char *key, const char *val, int indent) {
    for (int i = 0; i < indent; ++i) fputc(' ', f);
    json_str(f, key);
    fprintf(f, ": ");
    json_str(f, val);
    fputc('\n', f);
}

/* Trailing-comma helper: prints "," if more follows. */
static void comma(FILE *f, int more) { if (more) fputc(',', f); fputc('\n', f); }

/* ------------------------------------------------------------------- */
/* Section.                                                            */
/* ------------------------------------------------------------------- */
static void emit_section(FILE *f, const dt_section_t *s, int indent) {
    char buf[32];
    dt_hex(buf, sizeof(buf), s->rva);
    for (int i = 0; i < indent; ++i) fputc(' ', f);
    fprintf(f, "\"rva\": "); json_str(f, buf); fprintf(f, ",\n");
    dt_hex(buf, sizeof(buf), s->file_offset);
    for (int i = 0; i < indent; ++i) fputc(' ', f);
    fprintf(f, "\"file_offset\": "); json_str(f, buf); fprintf(f, ",\n");
    for (int i = 0; i < indent; ++i) fputc(' ', f);
    fprintf(f, "\"raw_size\": %u,\n", s->raw_size);
    for (int i = 0; i < indent; ++i) fputc(' ', f);
    fprintf(f, "\"virtual_size\": %u\n", s->virtual_size);
}

/* ------------------------------------------------------------------- */
/* Anchor sanity entry.                                                */
/* ------------------------------------------------------------------- */
static void emit_anchor_sanity(FILE *f, const dt_anchor_sanity_t *s,
                               int is_last) {
    fprintf(f, "    {\n");
    fprintf(f, "      \"label\": ");            json_str(f, s->label);            fprintf(f, ",\n");
    fprintf(f, "      \"string\": ");           json_str(f, s->string);           fprintf(f, ",\n");
    fprintf(f, "      \"documented_file_offset\": "); json_str(f, s->documented_file_offset); fprintf(f, ",\n");
    fprintf(f, "      \"actual_at_offset\": "); json_str(f, s->actual_at_offset); fprintf(f, ",\n");
    fprintf(f, "      \"computed_rva\": ");     json_str(f, s->computed_rva);     fprintf(f, ",\n");
    if (s->has_correction) {
        fprintf(f, "      \"actual_bytes_hex\": "); json_str(f, s->actual_bytes_hex); fprintf(f, ",\n");
        fprintf(f, "      \"doc_correction\": ");   json_str(f, s->doc_correction);
    } else {
        fprintf(f, "      \"doc_correction\": null");
    }
    fprintf(f, "\n    }");
    comma(f, !is_last);
}

/* ------------------------------------------------------------------- */
/* Category A entry.                                                   */
/* ------------------------------------------------------------------- */
static void emit_cat_a(FILE *f, const dt_cat_a_t *ca, int is_last) {
    fprintf(f, "    {\n");
    fprintf(f, "      \"anchor\": ");        json_str(f, ca->anchor);        fprintf(f, ",\n");
    fprintf(f, "      \"anchor_string\": "); json_str(f, ca->anchor_string); fprintf(f, ",\n");
    fprintf(f, "      \"anchor_rva\": ");    json_str(f, ca->anchor_rva);    fprintf(f, ",\n");
    fprintf(f, "      \"xref_count\": %d,\n", ca->xref_count);
    fprintf(f, "      \"xref_sites\": [");
    for (int i = 0; i < ca->xref_count; ++i) {
        fprintf(f, i ? ", " : " ");
        json_str(f, ca->xref_sites[i]);
    }
    fprintf(f, "%s],\n", ca->xref_count ? " " : "");
    fprintf(f, "      \"containing_functions\": [");
    if (ca->containing_count == 0) fprintf(f, "]");
    for (int i = 0; i < ca->containing_count; ++i) {
        fprintf(f, "%s\n        {\n", i ? "," : "");
        fprintf(f, "          \"begin_rva\": ");  json_str(f, ca->containing[i].begin_rva);  fprintf(f, ",\n");
        fprintf(f, "          \"end_rva\": ");    json_str(f, ca->containing[i].end_rva);    fprintf(f, ",\n");
        fprintf(f, "          \"size_bytes\": %d\n", ca->containing[i].size_bytes);
        fprintf(f, "        }");
    }
    if (ca->containing_count) fprintf(f, "\n      ]");
    fprintf(f, "\n    }");
    comma(f, !is_last);
}

/* ------------------------------------------------------------------- */
/* Call edge (init call graph).                                       */
/* ------------------------------------------------------------------- */
static void emit_call_edge(FILE *f, const dt_call_edge_t *e, int is_last) {
    fprintf(f, "    {\n");
    fprintf(f, "      \"call_site_rva\": "); json_str(f, e->call_site_rva); fprintf(f, ",\n");
    fprintf(f, "      \"kind\": ");          json_str(f, e->kind);          fprintf(f, ",\n");
    if (dt_streq(e->kind, "indirect")) {
        fprintf(f, "      \"operand\": ");   json_str(f, e->operand);       fprintf(f, ",\n");
        fprintf(f, "      \"note\": \"indirect call \xe2\x80\x94 target requires backward dataflow (out of scope for Phase 0; flag for Phase 2)\"\n");
    } else {
        fprintf(f, "      \"target_rva\": ");       json_str(f, e->target_rva);       fprintf(f, ",\n");
        fprintf(f, "      \"is_thunk\": %s,\n", e->is_thunk ? "true" : "false");
        fprintf(f, "      \"thunk_chain\": [");
        for (int i = 0; i < e->thunk_chain_len; ++i) {
            fprintf(f, i ? ", " : " ");
            json_str(f, e->thunk_chain[i]);
        }
        fprintf(f, "%s],\n", e->thunk_chain_len ? " " : "");
        fprintf(f, "      \"real_target_rva\": "); json_str(f, e->real_target_rva); fprintf(f, ",\n");
        fprintf(f, "      \"has_pdata\": %s,\n", e->has_pdata ? "true" : "false");
        if (e->has_pdata) {
            fprintf(f, "      \"func_begin\": "); json_str(f, e->func_begin); fprintf(f, ",\n");
            fprintf(f, "      \"func_end\": ");   json_str(f, e->func_end);   fprintf(f, ",\n");
            fprintf(f, "      \"func_size\": %d,\n", e->func_size);
        } else {
            fprintf(f, "      \"func_begin\": null,\n");
            fprintf(f, "      \"func_end\": null,\n");
            fprintf(f, "      \"func_size\": 0,\n");
        }
        if (e->has_import) {
            fprintf(f, "      \"import\": "); json_str(f, e->import_name); fprintf(f, ",\n");
        } else {
            fprintf(f, "      \"import\": null,\n");
        }
        /* arg_hints object */
        fprintf(f, "      \"arg_hints\": {");
        int first = 1;
        const char *labels[4] = {"rcx", "rdx", "r8", "r9"};
        const char *vals[4]   = {e->arg_rcx, e->arg_rdx, e->arg_r8, e->arg_r9};
        for (int k = 0; k < 4; ++k) {
            if (vals[k][0] == '\0') continue;
            fprintf(f, "%s\n        ", first ? "" : ",");
            json_str(f, labels[k]);
            fprintf(f, ": ");
            json_str(f, vals[k]);
            first = 0;
        }
        fprintf(f, first ? "}\n" : "\n      }\n");
    }
    fprintf(f, "    }");
    comma(f, !is_last);
}

/* ------------------------------------------------------------------- */
/* Classified target.                                                  */
/* ------------------------------------------------------------------- */
static void emit_classified(FILE *f, const dt_classified_t *c, int is_last) {
    fprintf(f, "    {\n");
    fprintf(f, "      \"target_rva\": ");       json_str(f, c->target_rva);       fprintf(f, ",\n");
    fprintf(f, "      \"real_target_rva\": ");  json_str(f, c->real_target_rva);  fprintf(f, ",\n");
    fprintf(f, "      \"is_thunk\": %s,\n", c->is_thunk ? "true" : "false");
    fprintf(f, "      \"thunk_chain\": [");
    for (int i = 0; i < c->thunk_chain_len; ++i) {
        fprintf(f, i ? ", " : " ");
        json_str(f, c->thunk_chain[i]);
    }
    fprintf(f, "%s],\n", c->thunk_chain_len ? " " : "");
    fprintf(f, "      \"has_pdata\": %s,\n", c->has_pdata ? "true" : "false");
    if (c->has_pdata) {
        fprintf(f, "      \"func_begin\": "); json_str(f, c->func_begin); fprintf(f, ",\n");
        fprintf(f, "      \"func_end\": ");   json_str(f, c->func_end);   fprintf(f, ",\n");
        fprintf(f, "      \"func_size\": %d,\n", c->func_size);
    } else {
        fprintf(f, "      \"func_begin\": null,\n");
        fprintf(f, "      \"func_end\": null,\n");
        fprintf(f, "      \"func_size\": 0,\n");
    }
    if (c->has_import) {
        fprintf(f, "      \"import\": "); json_str(f, c->import_name); fprintf(f, ",\n");
    } else {
        fprintf(f, "      \"import\": null,\n");
    }
    fprintf(f, "      \"call_sites\": [");
    for (int i = 0; i < c->call_site_count; ++i) {
        fprintf(f, i ? ", " : " ");
        json_str(f, c->call_sites[i]);
    }
    fprintf(f, "%s],\n", c->call_site_count ? " " : "");
    fprintf(f, "      \"arg_hints_sample\": {");
    int first = 1;
    const char *labels[4] = {"rcx", "rdx", "r8", "r9"};
    const char *vals[4]   = {c->arg_rcx, c->arg_rdx, c->arg_r8, c->arg_r9};
    for (int k = 0; k < 4; ++k) {
        if (vals[k][0] == '\0') continue;
        fprintf(f, "%s\n        ", first ? "" : ",");
        json_str(f, labels[k]);
        fprintf(f, ": ");
        json_str(f, vals[k]);
        first = 0;
    }
    fprintf(f, first ? "},\n" : "\n      },\n");
    fprintf(f, "      \"classification\": ");   json_str(f, c->classification);   fprintf(f, ",\n");
    fprintf(f, "      \"confidence\": ");       json_str(f, c->confidence);       fprintf(f, ",\n");
    fprintf(f, "      \"evidence\": ");         json_str(f, c->evidence);
    if (c->internal_lua_load_rva[0]) {
        fprintf(f, ",\n      \"internal_lua_load_rva\": ");
        json_str(f, c->internal_lua_load_rva);
    }
    fprintf(f, "\n    }");
    comma(f, !is_last);
}

/* ------------------------------------------------------------------- */
/* Category B candidate.                                              */
/* ------------------------------------------------------------------- */
static void emit_cat_b(FILE *f, const dt_cat_b_t *cb, int is_last) {
    fprintf(f, "    {\n");
    fprintf(f, "      \"name\": ");             json_str(f, cb->name);             fprintf(f, ",\n");
    fprintf(f, "      \"candidate_rvas\": [");
    for (int i = 0; i < cb->candidate_rva_count; ++i) {
        fprintf(f, i ? ", " : " ");
        json_str(f, cb->candidate_rvas[i]);
    }
    fprintf(f, "%s],\n", cb->candidate_rva_count ? " " : "");
    fprintf(f, "      \"confidence\": ");       json_str(f, cb->confidence);       fprintf(f, ",\n");
    fprintf(f, "      \"evidence\": ");         json_str(f, cb->evidence);         fprintf(f, ",\n");
    fprintf(f, "      \"discovery_method\": "); json_str(f, cb->discovery_method);
    if (dt_streq(cb->name, "lua_newstate") && cb->has_trace) {
        fprintf(f, ",\n      \"thunk_entry_rva\": ");
        json_str(f, cb->thunk_entry_rva);
        fprintf(f, ",\n      \"real_body_rva\": ");
        json_str(f, cb->real_body_rva);
        fprintf(f, ",\n      \"body_size_bytes\": %d,\n", cb->body_size_bytes);
        fprintf(f, "      \"trace\": {\n");
        fprintf(f, "        \"atpanic_call_rva\": ");   json_str(f, cb->trace.atpanic_call_rva);   fprintf(f, ",\n");
        fprintf(f, "        \"rcx_load_rva\": ");      json_str(f, cb->trace.rcx_load_rva);      fprintf(f, ",\n");
        fprintf(f, "        \"rcx_source\": ");        json_str(f, cb->trace.rcx_source);        fprintf(f, ",\n");
        fprintf(f, "        \"l_slot\": ");            json_str(f, cb->trace.l_slot);            fprintf(f, ",\n");
        fprintf(f, "        \"store_rva\": ");         json_str(f, cb->trace.store_rva);         fprintf(f, ",\n");
        fprintf(f, "        \"newstate_call_rva\": "); json_str(f, cb->trace.newstate_call_rva); fprintf(f, "\n");
        fprintf(f, "      }");
    } else if (dt_streq(cb->name, "luaL_loadbuffer") && cb->internal_lua_load_rva[0]) {
        fprintf(f, ",\n      \"internal_lua_load_rva\": ");
        json_str(f, cb->internal_lua_load_rva);
    }
    fprintf(f, "\n    }");
    comma(f, !is_last);
}

/* ------------------------------------------------------------------- */
/* Top-level serializer.                                               */
/* ------------------------------------------------------------------- */
int dt_write_json(const dt_result_t *r, FILE *f) {
    fprintf(f, "{\n");

    /* binary */
    fprintf(f, "  \"binary\": {\n");
    fprintf(f, "    \"path\": ");       json_str(f, r->binary_path); fprintf(f, ",\n");
    fprintf(f, "    \"size_bytes\": %u,\n", r->binary_size);
    fprintf(f, "    \"sha256\": ");     json_str(f, r->sha256);      fprintf(f, "\n");
    fprintf(f, "  },\n");

    /* pe_sections */
    fprintf(f, "  \"pe_sections\": {\n");
    fprintf(f, "    \".text\": {\n");   emit_section(f, &r->text,  6); fprintf(f, "    },\n");
    fprintf(f, "    \".rdata\": {\n");  emit_section(f, &r->rdata, 6); fprintf(f, "    },\n");
    fprintf(f, "    \".pdata\": {\n");  emit_section(f, &r->pdata, 6); fprintf(f, "    }\n");
    fprintf(f, "  },\n");

    fprintf(f, "  \"rdata_delta\": %u,\n", r->rdata_delta);
    fprintf(f, "  \"rdata_delta_matches_doc\": %s,\n",
            r->rdata_delta_matches_doc ? "true" : "false");

    /* anchor_sanity */
    fprintf(f, "  \"anchor_sanity\": [\n");
    for (int i = 0; i < r->anchor_sanity_count; ++i)
        emit_anchor_sanity(f, &r->anchor_sanity[i],
                           i + 1 == r->anchor_sanity_count);
    fprintf(f, "  ],\n");

    /* category_a_functions */
    fprintf(f, "  \"category_a_functions\": [\n");
    for (int i = 0; i < r->cat_a_count; ++i)
        emit_cat_a(f, &r->cat_a[i], i + 1 == r->cat_a_count);
    fprintf(f, "  ],\n");

    /* init_candidate */
    fprintf(f, "  \"init_candidate\": ");
    if (!r->init.found) {
        fprintf(f, "null,\n");
    } else {
        char panic_buf[32];
        dt_hex(panic_buf, sizeof(panic_buf), r->init.lua_panic_body_rva);
        fprintf(f, "{\n");
        fprintf(f, "    \"begin_rva\": ");   json_str(f, r->init.begin_rva);   fprintf(f, ",\n");
        fprintf(f, "    \"end_rva\": ");     json_str(f, r->init.end_rva);     fprintf(f, ",\n");
        fprintf(f, "    \"size_bytes\": %d,\n", r->init.size_bytes);
        fprintf(f, "    \"reasoning\": ");   json_str(f, r->init.reasoning);   fprintf(f, ",\n");
        fprintf(f, "    \"lua_environment_marker_found\": %s,\n",
                r->init.lua_environment_marker_found ? "true" : "false");
        fprintf(f, "    \"lua_panic_body_rva\": "); json_str(f, panic_buf); fprintf(f, ",\n");
        fprintf(f, "    \"lea_of_lua_panic_sites\": [");
        for (int i = 0; i < r->init.lea_of_lua_panic_count; ++i) {
            fprintf(f, i ? ", " : " ");
            json_str(f, r->init.lea_of_lua_panic_sites[i]);
        }
        fprintf(f, "%s]\n", r->init.lea_of_lua_panic_count ? " " : "");
        fprintf(f, "  },\n");
    }

    /* category_b_candidates */
    fprintf(f, "  \"category_b_candidates\": [\n");
    for (int i = 0; i < r->cat_b_count; ++i)
        emit_cat_b(f, &r->cat_b[i], i + 1 == r->cat_b_count);
    fprintf(f, "  ],\n");

    /* init_candidate_call_graph */
    fprintf(f, "  \"init_candidate_call_graph\": [\n");
    for (int i = 0; i < r->call_graph_count; ++i)
        emit_call_edge(f, &r->call_graph[i],
                       i + 1 == r->call_graph_count);
    fprintf(f, "  ],\n");

    /* classified_call_targets */
    fprintf(f, "  \"classified_call_targets\": [\n");
    for (int i = 0; i < r->classified_count; ++i)
        emit_classified(f, &r->classified[i],
                        i + 1 == r->classified_count);
    fprintf(f, "  ],\n");

    /* luajit_error_string_crosscheck */
    fprintf(f, "  \"luajit_error_string_crosscheck\": {\n");
    fprintf(f, "    \"error_string_results\": [\n");
    for (int i = 0; i < r->phase_d.result_count; ++i) {
        const dt_err_string_result_t *e = &r->phase_d.results[i];
        fprintf(f, "      {\n");
        fprintf(f, "        \"label\": ");               json_str(f, e->label);               fprintf(f, ",\n");
        fprintf(f, "        \"string\": ");              json_str(f, e->string);              fprintf(f, ",\n");
        fprintf(f, "        \"documented_file_offset\": "); json_str(f, e->documented_file_offset); fprintf(f, ",\n");
        fprintf(f, "        \"computed_rva\": ");        json_str(f, e->computed_rva);        fprintf(f, ",\n");
        fprintf(f, "        \"string_at_offset\": %s,\n", e->string_at_offset ? "true" : "false");
        fprintf(f, "        \"lea_xref_count\": %d,\n", e->lea_xref_count);
        fprintf(f, "        \"lea_xref_sites\": [],\n");
        fprintf(f, "        \"pointer_table_hits\": %d\n", e->pointer_table_hits);
        fprintf(f, "      }%s\n", i + 1 == r->phase_d.result_count ? "" : ",");
    }
    fprintf(f, "    ],\n");
    fprintf(f, "    \"any_xref_found\": %s,\n",
            r->phase_d.any_xref_found ? "true" : "false");
    fprintf(f, "    \"methodology_note\": "); json_str(f, r->phase_d.methodology_note); fprintf(f, "\n");
    fprintf(f, "  },\n");

    /* lua_pcall_clustering: the per-candidate survey + summary. Previously
     * computed by dt_phase_lua_pcall but not serialized; emitting it here
     * makes Phase 2a's clustering work visible in the JSON output (the
     * cat_b[lua_pcall] entry summarizes; this section shows all candidates). */
    fprintf(f, "  \"lua_pcall_clustering\": {\n");
    fprintf(f, "    \"candidates\": [");
    if (r->pcall_candidate_count == 0) fprintf(f, "]");
    for (int i = 0; i < r->pcall_candidate_count; ++i) {
        const dt_pcall_candidate_t *c = &r->pcall_candidates[i];
        char rva_buf[32];
        dt_hex(rva_buf, sizeof(rva_buf), c->rva);
        fprintf(f, "%s\n      {\n", i ? "," : "");
        fprintf(f, "        \"rva\": ");     json_str(f, rva_buf);     fprintf(f, ",\n");
        fprintf(f, "        \"score\": %d,\n", c->score);
        fprintf(f, "        \"reasoning\": "); json_str(f, c->reasoning); fprintf(f, "\n");
        fprintf(f, "      }");
    }
    if (r->pcall_candidate_count) fprintf(f, "\n    ],\n");
    else fprintf(f, ",\n");
    fprintf(f, "    \"summary\": "); json_str(f, r->pcall_summary); fprintf(f, ",\n");
    fprintf(f, "    \"candidate_count\": %d\n", r->pcall_candidate_count);
    fprintf(f, "  },\n");

    /* doc_corrections */
    fprintf(f, "  \"doc_corrections\": [");
    for (int i = 0; i < r->doc_correction_count; ++i) {
        fprintf(f, i ? ", " : " ");
        json_str(f, r->doc_corrections[i]);
    }
    fprintf(f, "%s],\n", r->doc_correction_count ? " " : "");

    /* methodology_gaps */
    fprintf(f, "  \"methodology_gaps\": [\n");
    for (int i = 0; i < r->methodology_gap_count; ++i) {
        fprintf(f, "    ");
        json_str(f, r->methodology_gaps[i]);
        fprintf(f, "%s\n", i + 1 == r->methodology_gap_count ? "" : ",");
    }
    fprintf(f, "  ]\n");

    fprintf(f, "}\n");
    return 0;
}
