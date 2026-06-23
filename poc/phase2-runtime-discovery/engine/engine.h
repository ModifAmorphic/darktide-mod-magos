/*
 * engine.h - Darktide Lua VM Injection discovery engine (Phase 2a).
 *
 * Pure in-memory PE/CFG analysis. NO file I/O, NO POSIX, NO Windows deps.
 * The engine consumes a flat image buffer that has been laid out by RVA
 * (i.e. image[rva] is the byte at that RVA). The offline tool wrapper in
 * tool/discover.c produces such a buffer by copying each PE section to
 * its VirtualAddress; Phase 2b's injected DLL will pass the in-process
 * module base directly (Windows already lays modules out by RVA).
 *
 * Behavioural spec: mirrors poc/phase0-offline-discovery/discover.py
 * exactly. Cross-check oracle: addresses.json in the same directory.
 */
#ifndef DARKTIDE_ENGINE_H
#define DARKTIDE_ENGINE_H

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ------------------------------------------------------------------ */
/* Compile-time constants pinned to the analyzed build.                */
/* ------------------------------------------------------------------ */
#define DT_EXPECTED_FILE_SIZE   18715784u
#define DT_EXPECTED_RDATA_DELTA 0x2A00u

#define DT_MAX_ANCHORS          16
#define DT_MAX_XREF_SITES       64
#define DT_MAX_CONTAINING_FUNCS 8
#define DT_MAX_CAT_A            16
#define DT_MAX_CAT_B            16
#define DT_MAX_CALL_GRAPH       256
#define DT_MAX_CLASSIFIED       128
#define DT_MAX_THUNK_CHAIN      16
#define DT_MAX_RVAS_PER_FUNC    4
#define DT_MAX_LEA_SITES        32
#define DT_MAX_METHOD_GAPS      16
#define DT_MAX_DOC_CORRECTIONS  8
#define DT_MAX_ERR_STRINGS      8
#define DT_MAX_LUA_PCALL_CANDS  8

#define DT_STR_LONG             1024
#define DT_STR_MED              512
#define DT_STR_SHORT            128

/* ------------------------------------------------------------------ */
/* PE section model.                                                   */
/* ------------------------------------------------------------------ */
typedef struct {
    char     name[16];
    uint32_t rva;
    uint32_t file_offset;
    uint32_t raw_size;
    uint32_t virtual_size;
} dt_section_t;

/* One RUNTIME_FUNCTION entry from .pdata. */
typedef struct {
    uint32_t begin;
    uint32_t end;
    uint32_t unwind;
} dt_runtime_function_t;

/* ------------------------------------------------------------------ */
/* Anchor table entry (transcribed verbatim from anchors doc S3/S5).   */
/* ------------------------------------------------------------------ */
typedef struct {
    const char *label;
    uint32_t    documented_file_offset;
    const char *expected;        /* expected string at that offset */
} dt_anchor_def_t;

extern const dt_anchor_def_t dt_anchors_s3[];
extern const int             dt_anchors_s3_count;
extern const dt_anchor_def_t dt_error_strings_s5[];
extern const int             dt_error_strings_s5_count;

/* ------------------------------------------------------------------ */
/* Anchor sanity (Phase A).                                            */
/* ------------------------------------------------------------------ */
typedef struct {
    char     label[DT_STR_SHORT];
    char     string[DT_STR_MED];
    char     documented_file_offset[DT_STR_SHORT]; /* hex */
    char     actual_at_offset[8];                   /* "yes"/"no" */
    char     computed_rva[DT_STR_SHORT];            /* hex */
    char     doc_correction[DT_STR_LONG];
    int      has_correction;
    char     actual_bytes_hex[DT_STR_SHORT];
} dt_anchor_sanity_t;

/* ------------------------------------------------------------------ */
/* Category A: per-anchor xref -> containing functions (Phase B).      */
/* ------------------------------------------------------------------ */
typedef struct {
    char begin_rva[DT_STR_SHORT];
    char end_rva[DT_STR_SHORT];
    int  size_bytes;
} dt_func_range_t;

typedef struct {
    char             anchor[DT_STR_SHORT];
    char             anchor_string[DT_STR_MED];
    char             anchor_rva[DT_STR_SHORT];
    int              xref_count;
    char             xref_sites[DT_MAX_XREF_SITES][DT_STR_SHORT];
    int              containing_count;
    dt_func_range_t  containing[DT_MAX_CONTAINING_FUNCS];
} dt_cat_a_t;

/* ------------------------------------------------------------------ */
/* Init candidate (Phase C).                                           */
/* ------------------------------------------------------------------ */
typedef struct {
    uint32_t begin;
    uint32_t end;
    int      size_bytes;
    int      found;
    int      lua_environment_marker_found;
    uint32_t lua_panic_body_rva;
    char     lea_of_lua_panic_sites[DT_MAX_LEA_SITES][DT_STR_SHORT];
    int      lea_of_lua_panic_count;
    char     begin_rva[DT_STR_SHORT];
    char     end_rva[DT_STR_SHORT];
    char     reasoning[DT_STR_LONG];
} dt_init_t;

/* ------------------------------------------------------------------ */
/* Call graph (init candidate body).                                   */
/* ------------------------------------------------------------------ */
typedef struct {
    char call_site_rva[DT_STR_SHORT];
    char kind[16];                          /* "direct"/"indirect" */
    char operand[DT_STR_SHORT];
    char target_rva[DT_STR_SHORT];          /* direct only */
    int  is_thunk;
    char thunk_chain[DT_MAX_THUNK_CHAIN][DT_STR_SHORT];
    int  thunk_chain_len;
    char real_target_rva[DT_STR_SHORT];
    int  has_pdata;
    char func_begin[DT_STR_SHORT];
    char func_end[DT_STR_SHORT];
    int  func_size;
    char import_name[DT_STR_MED];          /* "DLL!func" or empty */
    int  has_import;
    /* arg hints: last-write to rcx/rdx/r8/r9 within 8 preceding insns */
    char arg_rcx[DT_STR_MED];
    char arg_rdx[DT_STR_MED];
    char arg_r8[DT_STR_MED];
    char arg_r9[DT_STR_MED];
} dt_call_edge_t;

/* ------------------------------------------------------------------ */
/* Classified call target (grouped by real_target).                    */
/* ------------------------------------------------------------------ */
typedef struct {
    char target_rva[DT_STR_SHORT];
    char real_target_rva[DT_STR_SHORT];
    int  is_thunk;
    char thunk_chain[DT_MAX_THUNK_CHAIN][DT_STR_SHORT];
    int  thunk_chain_len;
    int  has_pdata;
    char func_begin[DT_STR_SHORT];
    char func_end[DT_STR_SHORT];
    int  func_size;
    char import_name[DT_STR_MED];          /* "DLL!func" */
    int  has_import;
    char call_sites[DT_MAX_XREF_SITES][DT_STR_SHORT];
    int  call_site_count;
    char arg_rcx[DT_STR_MED];
    char arg_rdx[DT_STR_MED];
    char arg_r8[DT_STR_MED];
    char arg_r9[DT_STR_MED];
    /* classification */
    char classification[DT_STR_SHORT];
    char confidence[DT_STR_SHORT];
    char evidence[DT_STR_LONG];
    char internal_lua_load_rva[DT_STR_SHORT]; /* set for load_wrapper */
} dt_classified_t;

/* ------------------------------------------------------------------ */
/* Category B candidate (the discovered LuaJIT API functions).         */
/* ------------------------------------------------------------------ */
typedef struct {
    char name[DT_STR_SHORT];
    char candidate_rvas[DT_MAX_RVAS_PER_FUNC][DT_STR_SHORT];
    int  candidate_rva_count;
    char confidence[DT_STR_SHORT];
    char evidence[DT_STR_LONG];
    char discovery_method[DT_STR_SHORT];
    /* lua_newstate-specific trace data */
    char thunk_entry_rva[DT_STR_SHORT];
    char real_body_rva[DT_STR_SHORT];
    int  body_size_bytes;
    int  has_trace;
    struct {
        char atpanic_call_rva[DT_STR_SHORT];
        char rcx_load_rva[DT_STR_SHORT];
        char rcx_source[DT_STR_SHORT];
        char l_slot[DT_STR_SHORT];
        char store_rva[DT_STR_SHORT];
        char newstate_call_rva[DT_STR_SHORT];
    } trace;
    /* luaL_loadbuffer-specific */
    char internal_lua_load_rva[DT_STR_SHORT];
} dt_cat_b_t;

/* ------------------------------------------------------------------ */
/* Phase D: error-string cross-check (negative result).               */
/* ------------------------------------------------------------------ */
typedef struct {
    char     label[DT_STR_SHORT];
    char     string[DT_STR_MED];
    char     documented_file_offset[DT_STR_SHORT];
    char     computed_rva[DT_STR_SHORT];
    int      string_at_offset;
    int      lea_xref_count;
    int      pointer_table_hits;
} dt_err_string_result_t;

typedef struct {
    dt_err_string_result_t results[DT_MAX_ERR_STRINGS];
    int                    result_count;
    int                    any_xref_found;
    char                   methodology_note[DT_STR_LONG];
} dt_phase_d_t;

/* ------------------------------------------------------------------ */
/* lua_pcall clustering outcome.                                       */
/* ------------------------------------------------------------------ */
typedef struct {
    uint32_t rva;
    int      score;
    char     reasoning[DT_STR_LONG];
} dt_pcall_candidate_t;

/* ------------------------------------------------------------------ */
/* Top-level discovery result.                                         */
/* ------------------------------------------------------------------ */
typedef struct {
    /* Binary metadata (path/sha/size are filled by the tool wrapper). */
    char     binary_path[DT_STR_LONG];
    uint32_t binary_size;
    char     sha256[65];

    /* PE */
    uint64_t                 image_base;
    dt_section_t             text;
    dt_section_t             rdata;
    dt_section_t             pdata;
    uint32_t                 rdata_delta;
    int                      rdata_delta_matches_doc;
    uint32_t                 runtime_function_count;

    /* Phase A */
    dt_anchor_sanity_t       anchor_sanity[DT_MAX_ANCHORS];
    int                      anchor_sanity_count;
    char                     doc_corrections[DT_MAX_DOC_CORRECTIONS][DT_STR_LONG];
    int                      doc_correction_count;

    /* Phase B */
    dt_cat_a_t               cat_a[DT_MAX_CAT_A];
    int                      cat_a_count;

    /* Phase C */
    dt_init_t                init;
    dt_call_edge_t           call_graph[DT_MAX_CALL_GRAPH];
    int                      call_graph_count;
    dt_classified_t          classified[DT_MAX_CLASSIFIED];
    int                      classified_count;
    dt_cat_b_t               cat_b[DT_MAX_CAT_B];
    int                      cat_b_count;

    /* Phase D */
    dt_phase_d_t             phase_d;

    /* Methodology gaps (prose strings). */
    char                     methodology_gaps[DT_MAX_METHOD_GAPS][DT_STR_LONG];
    int                      methodology_gap_count;

    /* lua_pcall clustering attempt outcome (separate from cat_b for the
       detailed reasoning log; the cat_b[lua_pcall] entry summarizes). */
    dt_pcall_candidate_t     pcall_candidates[DT_MAX_LUA_PCALL_CANDS];
    int                      pcall_candidate_count;
    char                     pcall_summary[DT_STR_LONG];
} dt_result_t;

/* ------------------------------------------------------------------ */
/* Engine API.                                                         */
/* ------------------------------------------------------------------ */
/*
 * Run the full discovery pipeline on an in-memory image.
 *
 *   image      : buffer where image[rva] is the byte at that RVA.
 *   image_size : size of the buffer (>= OptionalHeader.SizeOfImage).
 *   image_base : OptionalHeader.ImageBase (used only for IAT VA matching).
 *
 * Returns 0 on success, non-zero on fatal error (size mismatch, missing
 * sections, PE parse failure). On success the result struct is fully
 * populated; the caller may then call dt_write_json / dt_write_report.
 *
 * The buffer must outlive any use of the result struct (strings reference
 * anchor table literals, but the engine does not retain image pointers
 * after return, so the buffer can be freed once dt_write_json/report run).
 */
int dt_discover(const uint8_t *image, size_t image_size, uint64_t image_base,
                dt_result_t *result);

/* Serialize the result as JSON to `out` (Phase 0 schema, field-for-field). */
int dt_write_json(const dt_result_t *result, FILE *out);

/* Serialize a human-readable markdown report to `out`. */
int dt_write_report(const dt_result_t *result, FILE *out);

/* SHA-256 of `data[0..len-1]` as a 64-char lowercase hex string (NUL-term). */
void dt_sha256_hex(const uint8_t *data, size_t len, char *out65);

/* ------------------------------------------------------------------ */
/* Engine context (internal helpers operate on this; see               */
/* engine_internal.h). Made public here so tests can drive individual   */
/* phases if desired.                                                   */
/* ------------------------------------------------------------------ */
typedef struct {
    const uint8_t      *image;
    size_t              image_size;
    uint64_t            image_base;
    const dt_section_t *text;
    const dt_section_t *rdata;
    const dt_section_t *pdata;
    const dt_runtime_function_t *runtime_functions;
    uint32_t            runtime_function_count;
} dt_engine_ctx_t;

/* Returns pointer to image byte at rva, or NULL if out of range. */
const uint8_t *dt_image_at(const dt_engine_ctx_t *ctx, uint32_t rva, size_t n);

/* Binary-search the .pdata table; returns NULL if rva is in a gap. */
const dt_runtime_function_t *dt_find_runtime_function(
    const dt_engine_ctx_t *ctx, uint32_t rva);

/* Translate an RVA to a buffer offset inside ctx->image. */
size_t dt_rva_to_offset(const dt_engine_ctx_t *ctx, uint32_t rva);

#ifdef __cplusplus
}
#endif

#endif /* DARKTIDE_ENGINE_H */
