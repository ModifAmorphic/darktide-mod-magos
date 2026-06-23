/*
 * engine_internal.h - Internal API used across engine TUs.
 *
 * The public API (engine.h) exposes only dt_discover/dt_write_json/
 * dt_write_report plus a few lookup helpers used by tests. Everything
 * here is internal plumbing shared between pe.c, scan.c, disasm.c,
 * classify.c, lua_pcall.c, json.c, report.c, discover.c.
 *
 * A discovery run is single-threaded: dt_engine_setup() builds a
 * process-wide singleton `dt_engine_state_t` (defined in pe.c) holding
 * the parsed PE / .pdata / imports; helpers in other TUs reach it via
 * dt_engine_ctx_active(). dt_discover() calls dt_engine_setup() exactly
 * once and tears down implicitly when it returns (the next call resets).
 */
#ifndef DARKTIDE_ENGINE_INTERNAL_H
#define DARKTIDE_ENGINE_INTERNAL_H

#include "engine.h"
#include <capstone/capstone.h>

/* ---- Singleton state (pe.c) --------------------------------------- */
/* Sets up the engine context. Returns 0 on success. After this call,
 * dt_engine_ctx_active() returns a pointer to the populated context. */
int dt_engine_setup(const uint8_t *image, size_t image_size,
                    uint64_t image_base,
                    const dt_section_t **out_text,
                    const dt_section_t **out_rdata,
                    const dt_section_t **out_pdata,
                    const dt_runtime_function_t **out_rf,
                    uint32_t *out_rf_count);

const dt_engine_ctx_t *dt_engine_ctx_active(void);

const dt_section_t *dt_section_text(void);
const dt_section_t *dt_section_rdata(void);
const dt_section_t *dt_section_pdata(void);

/* Import-table cache access (for import-thunk resolution). */
typedef struct {
    int      is_ordinal;
    uint32_t iat_rva;
    int      dll_idx;
    uint16_t hint;
    char     name[128];
} dt_import_entry_t;

typedef struct {
    char     dll[64];
    uint32_t iat_rva_start;
    uint32_t iat_rva_end;
} dt_import_dll_t;

typedef struct {
    int               ready;
    dt_import_dll_t   dlls[256];
    int               dll_count;
    dt_import_entry_t entries[8192];
    int               entry_count;
} dt_import_cache_t;

const dt_import_cache_t *dt_get_import_cache(void);
int dt_import_lookup(uint32_t iat_rva,
                     const char **dll_out, const char **name_out);

/* ---- Disassembly (disasm.c) --------------------------------------- */
/*
 * Disassemble a function body into a flat instruction array.
 *
 * If end==0, the function is treated as a leaf (no .pdata): decode until
 * the first `ret`/`retn`, capped at max_insns. Otherwise decode [begin,end).
 *
 * Returns the number of decoded instructions (<= max_insns), or -1 on
 * capstone failure. The caller owns the `insns` buffer.
 */
int dt_disasm_range(uint32_t begin, uint32_t end,
                    cs_insn *insns, int max_insns);

/* Disassemble [begin, begin+len) regardless of ret; returns count. */
int dt_disasm_raw(uint32_t begin, uint32_t len,
                  cs_insn *insns, int max_insns);

/* Extract a RIP-relative target RVA from a LEA/call instruction.
 * Returns 1 and fills *target on success; 0 if not RIP-relative. */
int dt_lea_target_rva(const cs_insn *ins, uint32_t *target);

/* ---- Scan helpers (scan.c) ---------------------------------------- */
/* LEA-xref scanner: returns RVAs of `48/4C 8D .. .. .. ..` sites whose
 * RIP+7+disp32 == target_rva. Mirrors discover.py find_lea_xrefs. */
void dt_find_lea_xrefs(const dt_engine_ctx_t *ctx, uint32_t target_rva,
                       uint32_t *out_sites, int *inout_count, int max);

/* E8 caller scanner: RVAs of `E8 rel32` sites whose target == target_rva. */
void dt_find_callers(const dt_engine_ctx_t *ctx, uint32_t target_rva,
                     uint32_t *out_callers, int *inout_count, int max);

/* Follow E9 rel32 thunk chains. Returns final rva; chain filled if given. */
uint32_t dt_trace_thunk(const dt_engine_ctx_t *ctx, uint32_t rva,
                        uint32_t *chain, int *chain_len, int max_hops);

/* Resolve FF 25 disp32 import thunk. Returns 1 and fills dll/name on hit. */
int dt_resolve_import_thunk(const dt_engine_ctx_t *ctx, uint32_t rva,
                            char *dll_out, size_t dll_sz,
                            char *name_out, size_t name_sz);

/* Return raw bytes of a function bounded by .pdata (or leaf-guessed). */
const uint8_t *dt_function_bytes(const dt_engine_ctx_t *ctx,
                                 uint32_t begin, uint32_t end,
                                 size_t *out_len);

/* ---- Phase helpers (discover.c) ----------------------------------- */
void dt_phase_a(const dt_engine_ctx_t *ctx, dt_result_t *r);
void dt_phase_b(const dt_engine_ctx_t *ctx, dt_result_t *r);
void dt_phase_c_select_init(const dt_engine_ctx_t *ctx, dt_result_t *r);
void dt_phase_c_classify(const dt_engine_ctx_t *ctx, dt_result_t *r);
void dt_phase_d(const dt_engine_ctx_t *ctx, dt_result_t *r);
void dt_phase_lua_pcall(const dt_engine_ctx_t *ctx, dt_result_t *r);
void dt_phase_methodology_gaps(dt_result_t *r);

/* ---- classify.c (also used by discover.c orchestrator) ------------- */
/* Enumerate direct+indirect calls in [begin,end); fills out[] up to max. */
int dt_enumerate_calls(uint32_t begin, uint32_t end,
                       dt_call_edge_t *out, int max);

/* lua_newstate backward-dataflow trace; fills *out. */
void dt_identify_lua_newstate(const dt_engine_ctx_t *ctx, dt_result_t *r,
                              const dt_classified_t *classified,
                              int n_classified, dt_cat_b_t *out);

/* luaL_loadbuffer trace from lua_resource::bytecode; fills *out. */
void dt_find_loadbuffer_from_bytecode(const dt_engine_ctx_t *ctx,
                                      dt_result_t *r, dt_cat_b_t *out);

/* Helper to build a cat_b entry from a classified target. */
void dt_classified_to_cat_b(const dt_classified_t *c, const char *name,
                            const char *method, dt_cat_b_t *out);

#endif /* DARKTIDE_ENGINE_INTERNAL_H */
