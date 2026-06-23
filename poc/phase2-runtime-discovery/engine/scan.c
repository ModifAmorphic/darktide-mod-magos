/*
 * scan.c - .text byte-pattern scanners and thunk resolvers.
 *
 * Mirrors discover.py: find_lea_xrefs, find_callers, trace_thunk,
 * resolve_import_thunk. Operates on a ctx->image laid out by RVA.
 */
#include "engine_internal.h"
#include "util.h"

/* ------------------------------------------------------------------- */
/* LEA-xref scanner.                                                   */
/*                                                                     */
/* Match `48/4C 8D <modrm> <disp32>` where (modrm & 0xC7) == 0x05, i.e. */
/* RIP-relative with no SIB byte. Computes irva + 7 + disp32 and       */
/* compares to target_rva. Mirrors discover.py find_lea_xrefs exactly  */
/* (same byte-by-byte scan, same offset math).                         */
/* ------------------------------------------------------------------- */
/* In-memory, section padding in [rva+virtual_size, rva+align_up(virtual_size,
 * section_align)) is zero-filled by the Windows loader, whereas on-disk that
 * range is either the tail of raw_size (if raw_size > virtual_size) or absent.
 * Scanning past virtual_size would walk into zero bytes that don't represent
 * real code in the mapped layout. Use min(raw_size, virtual_size) as the scan
 * bound; the image_size early-return below stays as a hard safety bound. */
static uint32_t scan_bound(const dt_section_t *t) {
    return t->raw_size <= t->virtual_size ? t->raw_size : t->virtual_size;
}

void dt_find_lea_xrefs(const dt_engine_ctx_t *ctx, uint32_t target_rva,
                       uint32_t *out_sites, int *inout_count, int max) {
    const dt_section_t *t = ctx->text;
    if (!t) return;
    if ((size_t)t->rva + t->raw_size > ctx->image_size) return;
    const uint8_t *blob = ctx->image + t->rva;
    int n = (int)scan_bound(t);
    int count = *inout_count;
    int i = 0;
    while (i < n - 7) {
        uint8_t b0 = blob[i];
        if ((b0 == 0x48 || b0 == 0x4C) &&
            blob[i + 1] == 0x8D &&
            (blob[i + 2] & 0xC7) == 0x05) {
            int32_t disp = dt_load_i32le(blob + i + 3);
            uint32_t irva = (uint32_t)(t->rva + i);
            /* RIP = next insn addr = irva + 7; target = rip + disp. */
            if ((int64_t)irva + 7 + (int64_t)disp == (int64_t)target_rva) {
                if (count < max) out_sites[count] = irva;
                count++;
            }
        }
        i++;
    }
    *inout_count = count;
}

/* ------------------------------------------------------------------- */
/* E8 caller scanner.                                                  */
/* ------------------------------------------------------------------- */
void dt_find_callers(const dt_engine_ctx_t *ctx, uint32_t target_rva,
                     uint32_t *out_callers, int *inout_count, int max) {
    const dt_section_t *t = ctx->text;
    if (!t) return;
    if ((size_t)t->rva + t->raw_size > ctx->image_size) return;
    const uint8_t *blob = ctx->image + t->rva;
    int n = (int)scan_bound(t);
    int count = *inout_count;
    int i = 0;
    while (i < n - 5) {
        if (blob[i] == 0xE8) {
            int32_t rel = dt_load_i32le(blob + i + 1);
            uint32_t irva = (uint32_t)(t->rva + i);
            if ((int64_t)irva + 5 + (int64_t)rel == (int64_t)target_rva) {
                if (count < max) out_callers[count] = irva;
                count++;
            }
        }
        i++;
    }
    *inout_count = count;
}

/* ------------------------------------------------------------------- */
/* CFG/hot-patch thunk follower.                                       */
/*                                                                     */
/* If rva points at `E9 rel32 + cc padding`, follow up to max_hops.    */
/* Returns the final rva; chain (including starting rva) filled in.    */
/* ------------------------------------------------------------------- */
uint32_t dt_trace_thunk(const dt_engine_ctx_t *ctx, uint32_t rva,
                        uint32_t *chain, int *chain_len, int max_hops) {
    int len = 0;
    uint32_t cur = rva;
    if (chain && chain_len) {
        if (len < max_hops) chain[len] = cur;
        len++;
    }
    for (int hop = 0; hop < max_hops; ++hop) {
        /* Must be inside .text. */
        const dt_section_t *t = ctx->text;
        if (!t || cur < t->rva || cur >= t->rva + scan_bound(t)) break;
        const uint8_t *p = dt_image_at(ctx, cur, 5);
        if (!p) break;
        if (p[0] != 0xE9) break;
        int32_t rel = dt_load_i32le(p + 1);
        uint32_t nxt = cur + 5 + (uint32_t)rel;
        cur = nxt;
        if (chain && chain_len) {
            if (len < max_hops) chain[len] = cur;
            len++;
        }
    }
    if (chain_len) *chain_len = len;
    return cur;
}

/* ------------------------------------------------------------------- */
/* Import thunk resolver.                                              */
/*                                                                     */
/* If rva points at `FF 25 disp32` (jmp [rip+disp32]), the IAT entry   */
/* at (rva + 6 + disp) is read and resolved to DLL!function via the    */
/* pre-parsed import cache.                                            */
/* ------------------------------------------------------------------- */
int dt_resolve_import_thunk(const dt_engine_ctx_t *ctx, uint32_t rva,
                            char *dll_out, size_t dll_sz,
                            char *name_out, size_t name_sz) {
    const uint8_t *p = dt_image_at(ctx, rva, 6);
    if (!p) return 0;
    if (p[0] != 0xFF || p[1] != 0x25) return 0;
    int32_t disp = dt_load_i32le(p + 2);
    uint32_t iat_rva = rva + 6 + (uint32_t)disp;
    const char *dll = NULL, *name = NULL;
    if (!dt_import_lookup(iat_rva, &dll, &name)) return 0;
    if (dll_out)  dt_strncpy(dll_out,  dll  ? dll  : "", dll_sz);
    if (name_out) dt_strncpy(name_out, name ? name : "", name_sz);
    return 1;
}

/* ------------------------------------------------------------------- */
/* function_bytes: pointer + length for a [begin,end) range, or for a  */
/* leaf-guessed range (begin..begin+256) when end==0.                  */
/* ------------------------------------------------------------------- */
const uint8_t *dt_function_bytes(const dt_engine_ctx_t *ctx,
                                 uint32_t begin, uint32_t end,
                                 size_t *out_len) {
    if (end == 0) end = begin + 256;
    if (end < begin) return NULL;
    size_t len = (size_t)end - begin;
    const uint8_t *p = dt_image_at(ctx, begin, len);
    if (!p) return NULL;
    if (out_len) *out_len = len;
    return p;
}
