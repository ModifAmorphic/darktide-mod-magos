/*
 * disasm_check.c — see disasm_check.h for the contract.
 *
 * Minimal PE-x86-64 parser (DOS+OPT-PE64 headers + section table) +
 * capstone disassembly + shape match. No external state, no allocation
 * beyond the slurped file.
 */
#include "disasm_check.h"

#include <capstone/capstone.h>

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define DEFAULT_PE_PATH \
    "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe"

/* ---- PE section walker ---------------------------------------------- */

static int pe_rva_to_off(const uint8_t *base, size_t fsz, uint32_t rva,
                         uint32_t *out_off) {
    if (fsz < 0x40) return -1;
    uint32_t pe_off = *(uint32_t *)(base + 0x3c);
    if ((size_t)pe_off + 4 + 20 > fsz) return -1;
    if (memcmp(base + pe_off, "PE\0\0", 4) != 0) return -1;
    uint16_t n_sections = *(uint16_t *)(base + pe_off + 6);
    uint16_t opt_size   = *(uint16_t *)(base + pe_off + 20);
    uint32_t sect_tab   = pe_off + 4 + 20 + opt_size;
    for (int i = 0; i < n_sections; i++) {
        uint32_t off = sect_tab + (uint32_t)i * 40;
        if ((size_t)off + 40 > fsz) return -1;
        uint32_t vsz  = *(uint32_t *)(base + off + 8);
        uint32_t vaddr= *(uint32_t *)(base + off + 12);
        uint32_t rsz  = *(uint32_t *)(base + off + 16);
        uint32_t praw = *(uint32_t *)(base + off + 20);
        if (rva >= vaddr && rva < vaddr + vsz) {
            uint32_t delta = rva - vaddr;
            if (delta >= rsz) return -1;   /* in virtual but not raw (BSS) */
            *out_off = praw + delta;
            return 0;
        }
    }
    return -1;
}

/* ---- Shape matcher --------------------------------------------------- *
 * Each "feat" flag corresponds to one observable consequence of lua_pcall's
 * source (lj_api.c:1120). We require ALL of them for a match. */
typedef struct {
    int reads_glref_08;   /* [rcx+0x08] or [reg+0x08] right after mov reg,rcx */
    int reads_base_10;    /* [rcx+0x10] / [reg+0x10] */
    int reads_top_18;     /* [rcx+0x18] / [reg+0x18] */
    int reads_stack_24;   /* [rcx+0x24] / [reg+0x24] */
    int test_r9;          /* test r9d,r9d  or  test r9,r9 */
    int branch_jne;       /* jne after the test */
    int branch_jle;       /* jle after the test (negative errfunc path) */
    int lea_index_r9_x8;  /* lea reg, [reg + r9*8 ...]  (errfunc slot math) */
    int inc_r8;           /* inc r8d  (nresults+1) */
    int shl_sub_top_minus_nargs;  /* shl rax,3 ; sub rdx, rax  (api_call_base) */
    int direct_call_count;        /* # of direct `call rel32` */
} pcall_features_t;

static void classify_insn(const cs_insn *ins, pcall_features_t *feat,
                          int *saw_shl_rax_3) {
    const char *o = ins->op_str;

    /* memory operand offsets — capstone formats small displacements < 10
     * as plain decimal ("+ 8]") and >= 10 as hex ("+ 0x10]"). Accept both
     * forms. We rely on lua_pcall's source pattern never reusing these
     * offsets for unrelated reads inside its small body. */
    if (strstr(o, "+ 8]")    || strstr(o, "+ 0x8]"))  feat->reads_glref_08 = 1;
    if (strstr(o, "+ 0x10]")) feat->reads_base_10  = 1;
    if (strstr(o, "+ 0x18]")) feat->reads_top_18   = 1;
    if (strstr(o, "+ 0x24]")) feat->reads_stack_24 = 1;

    if (ins->id == X86_INS_TEST &&
        (strstr(o, "r9") || strstr(o, "r9d"))) feat->test_r9 = 1;

    if (ins->id == X86_INS_JNE) feat->branch_jne = 1;
    if (ins->id == X86_INS_JLE) feat->branch_jle = 1;

    /* lea reg, [reg + r9*8 ...]  (or r9*8 +/- disp) — the scale-8 r9 index
     * is the distinctive errfunc→TValue* arithmetic. */
    if (ins->id == X86_INS_LEA && strstr(o, "r9*8")) feat->lea_index_r9_x8 = 1;

    if (ins->id == X86_INS_INC && strstr(o, "r8")) feat->inc_r8 = 1;

    /* api_call_base = L->top - nargs*8:
     *   shl rax, 3   ; rax = nargs*8
     *   sub rdx, rax ; rdx = top - nargs*8 */
    if (ins->id == X86_INS_SHL &&
        (strstr(o, "rax, 3") || strstr(o, "eax, 3"))) *saw_shl_rax_3 = 1;
    if (*saw_shl_rax_3 && ins->id == X86_INS_SUB &&
        (strstr(o, "rdx, rax") || strstr(o, "edx, eax")))
        feat->shl_sub_top_minus_nargs = 1;

    if (ins->id == X86_INS_CALL) {
        /* direct call rel32 → capstone prints a bare hex target.
         * indirect call → operand starts with "[" or a register name. */
        if (o[0] == '0' || (o[0] >= '1' && o[0] <= '9')) {
            feat->direct_call_count++;
        }
    }
}

/* ---- Public entry ---------------------------------------------------- */

int disasm_check_lua_pcall(const char *pe_path, uint32_t rva) {
    const char *path = pe_path ? pe_path : DEFAULT_PE_PATH;
    FILE *fp = fopen(path, "rb");
    if (!fp) {
        printf("[disasm_check] FAIL: cannot open %s (%s)\n",
               path, strerror(errno));
        printf("[disasm_check] (set $DARKTIDE_EXE to point at Darktide.exe"
               " from a Steam install)\n");
        return 2;
    }
    fseek(fp, 0, SEEK_END);
    long fsz = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    if (fsz <= 0) { fclose(fp); return 2; }
    uint8_t *buf = (uint8_t *)malloc((size_t)fsz);
    if (!buf) { fclose(fp); return 2; }
    if (fread(buf, 1, (size_t)fsz, fp) != (size_t)fsz) {
        printf("[disasm_check] FAIL: short read on %s\n", path);
        fclose(fp); free(buf); return 2;
    }
    fclose(fp);

    uint32_t off = 0;
    if (pe_rva_to_off(buf, (size_t)fsz, rva, &off) != 0) {
        printf("[disasm_check] FAIL: RVA 0x%x not in any section of %s\n",
               rva, path);
        free(buf); return 1;
    }

    csh h;
    if (cs_open(CS_ARCH_X86, CS_MODE_64, &h) != CS_ERR_OK) {
        printf("[disasm_check] FAIL: cs_open failed\n");
        free(buf); return 2;
    }

    cs_insn *ins;
    size_t avail = (size_t)fsz - off;
    /* lua_pcall is ~50 instructions; decode 64 to be safe. */
    size_t n = cs_disasm(h, buf + off, avail, rva, 64, &ins);
    if (n == 0) {
        printf("[disasm_check] FAIL: no instructions decoded at RVA 0x%x\n", rva);
        cs_close(&h); free(buf); return 1;
    }

    pcall_features_t feat; memset(&feat, 0, sizeof(feat));
    int saw_shl_rax_3 = 0;
    for (size_t i = 0; i < n; i++)
        classify_insn(&ins[i], &feat, &saw_shl_rax_3);

    /* Report what we saw (always — useful for diagnosis). */
    printf("[disasm_check] decoded %zu insns at RVA 0x%x (file off 0x%x)\n",
           n, rva, off);
    printf("[disasm_check] features:"
           " glref_08=%d base_10=%d top_18=%d stack_24=%d"
           " test_r9=%d jne=%d jle=%d"
           " lea_r9_x8=%d inc_r8=%d"
           " shl_sub(top-nargs*8)=%d direct_calls=%d\n",
           feat.reads_glref_08, feat.reads_base_10, feat.reads_top_18, feat.reads_stack_24,
           feat.test_r9, feat.branch_jne, feat.branch_jle,
           feat.lea_index_r9_x8, feat.inc_r8,
           feat.shl_sub_top_minus_nargs, feat.direct_call_count);

    /* Required: all features present + exactly one direct call. */
    int ok = feat.reads_glref_08 && feat.reads_base_10 && feat.reads_top_18 &&
             feat.reads_stack_24 && feat.test_r9 && feat.branch_jne && feat.branch_jle &&
             feat.lea_index_r9_x8 && feat.inc_r8 && feat.shl_sub_top_minus_nargs &&
             feat.direct_call_count == 1;

    if (!ok) {
        printf("[disasm_check] FAIL: shape does not match lua_pcall\n");
        printf("[disasm_check] --- disassembly (first 40 insns) ---\n");
        size_t lim = n < 40 ? n : 40;
        for (size_t i = 0; i < lim; i++)
            printf("[disasm_check]   0x%06llx: %-8s %s\n",
                   (unsigned long long)ins[i].address,
                   ins[i].mnemonic, ins[i].op_str);
        printf("[disasm_check] --- end disassembly ---\n");
    } else {
        printf("[disasm_check] OK: shape matches lua_pcall @ RVA 0x%x\n", rva);
    }

    cs_free(ins, n);
    cs_close(&h);
    free(buf);
    return ok ? 0 : 1;
}

/* =====================================================================*
 *  luaL_openlibs shape matcher (Phase 5 Step 1)
 * =====================================================================*
 * Verify the function at `rva` has luaL_openlibs's compiled shape.
 * Implementation reuses the same PE+capstone plumbing; the feature set
 * is openlibs-specific.
 *
 * LuaJIT 2.1 lib_init.c source structure (v2.1 branch):
 *   loop 1: for each lj_lib_load[] entry:
 *             lua_pushcfunction(L, lib->func)
 *             lua_pushstring(L, lib->name)
 *             lua_call(L, 1, 0)
 *   luaL_findtable(L, LUA_REGISTRYINDEX, "_PRELOAD", 1)
 *   loop 2: for each lj_lib_preload[] entry:
 *             lua_pushcfunction(L, lib->func)
 *             lua_setfield(L, -2, lib->name)
 *   lua_pop(L, 1)  // tail-jumped as lua_settop(L, -2)
 *
 * Distinctive compiled features:
 *   - LEA r, [rip+disp32] targeting the "_PRELOAD" string (the only
 *     literal string ref inside the body, used by luaL_findtable).
 *   - >= 5 distinct direct `call rel32` targets (pushcfunction,
 *     pushstring, lua_call, findtable, setfield — settop is a tail-jmp).
 *   - At least one backward `jne` (loop back-edge — two loops, so usually
 *     two backward jne's, but one suffices for identification).
 *   - Small body (<= 0x200 bytes; source is ~12 lines).
 *   - All call targets within the LuaJIT API cluster [0xc70000, 0xc90000).
 */

/* The "_PRELOAD" string's RVA in Darktide.exe's .rdata. Pinned to the
 * analyzed build (SHA-256 132eed5f...). A game update would shift this
 * (and the function RVA) together; the disasm_check's job is to verify
 * the function-shape, not to re-derive the string address. */
#define OPENLIBS_PRELOAD_RVA  0xe8d678u

typedef struct {
    int has_preload_lea;        /* LEA r, [rip+disp32] -> _PRELOAD */
    int n_direct_calls;         /* count of `call rel32` */
    int n_distinct_call_tgts;   /* distinct direct call targets */
    int n_backward_jne;         /* backward `jne rel8/rel32` (loop edges) */
    int all_calls_in_cluster;   /* all direct calls land in [0xc70000,0xc90000) */
    uint32_t body_end_rva;      /* rva of the end of the last decoded insn */
} openlibs_features_t;

static void classify_openlibs_insn(const cs_insn *ins, openlibs_features_t *feat,
                                    uint32_t *call_targets, int *call_targets_n,
                                    int max_targets) {
    /* Track the function body end rva — first RET or TAIL JMP ends it. */
    feat->body_end_rva = (uint32_t)(ins->address + ins->size);

    if (ins->id == X86_INS_CALL && ins->op_str[0] >= '0' && ins->op_str[0] <= '9') {
        /* Direct call rel32 — capstone prints the absolute target. */
        feat->n_direct_calls++;
        /* Parse the target address from op_str (hex). */
        unsigned long long tgt = strtoull(ins->op_str, NULL, 0);
        if (tgt >= 0xc70000 && tgt < 0xc90000) {
            /* In the LuaJIT API cluster. */
        } else {
            feat->all_calls_in_cluster = 0;
        }
        /* Track distinct targets (bounded). */
        int found = 0;
        for (int i = 0; i < *call_targets_n; i++) {
            if (call_targets[i] == (uint32_t)tgt) { found = 1; break; }
        }
        if (!found && *call_targets_n < max_targets) {
            call_targets[(*call_targets_n)++] = (uint32_t)tgt;
        }
    }

    /* Backward `jne`: capstone emits `jne <addr>`; backward iff addr < ins->addr.
     * capstone's op_str for branches is the absolute target address. */
    if (ins->id == X86_INS_JNE) {
        unsigned long long tgt = strtoull(ins->op_str, NULL, 0);
        if (tgt < ins->address) feat->n_backward_jne++;
    }

    /* LEA r, [rip+disp32] targeting _PRELOAD (rva 0xe8d678).
     * Parse from op_str — matches the style of classify_insn() above
     * (no detail-mode dependency). capstone formats RIP-rel LEA as:
     *   "lea r8, [rip + 0x20e295]"   or   "lea r8, [rip - 0x1234]"
     * Compute target = insn.address + insn.size + signed_disp.
     * The 7-byte LEA (48 8D xx dd dd dd dd) has insn.size == 7. */
    if (ins->id == X86_INS_LEA && ins->size == 7) {
        const char *o = ins->op_str;
        const char *rip = strstr(o, "[rip");
        if (rip) {
            long long disp = 0;
            const char *plus = strchr(rip, '+');
            const char *minus = strchr(rip, '-');
            if (plus && (!minus || plus < minus)) {
                disp = (long long)strtoll(plus + 1, NULL, 0);
            } else if (minus) {
                disp = -(long long)strtoll(minus + 1, NULL, 0);
            }
            uint32_t target = (uint32_t)(ins->address + ins->size + disp);
            if (target == OPENLIBS_PRELOAD_RVA) {
                feat->has_preload_lea = 1;
            }
        }
    }
}

int disasm_check_luaL_openlibs(const char *pe_path, uint32_t rva) {
    const char *path = pe_path ? pe_path : DEFAULT_PE_PATH;
    FILE *fp = fopen(path, "rb");
    if (!fp) {
        printf("[disasm_check] FAIL: cannot open %s (%s)\n",
               path, strerror(errno));
        return 2;
    }
    fseek(fp, 0, SEEK_END);
    long fsz = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    if (fsz <= 0) { fclose(fp); return 2; }
    uint8_t *buf = (uint8_t *)malloc((size_t)fsz);
    if (!buf) { fclose(fp); return 2; }
    if (fread(buf, 1, (size_t)fsz, fp) != (size_t)fsz) {
        printf("[disasm_check] FAIL: short read on %s\n", path);
        fclose(fp); free(buf); return 2;
    }
    fclose(fp);

    uint32_t off = 0;
    if (pe_rva_to_off(buf, (size_t)fsz, rva, &off) != 0) {
        printf("[disasm_check] FAIL: RVA 0x%x not in any section of %s\n",
               rva, path);
        free(buf); return 1;
    }

    csh h;
    if (cs_open(CS_ARCH_X86, CS_MODE_64, &h) != CS_ERR_OK) {
        printf("[disasm_check] FAIL: cs_open failed\n");
        free(buf); return 2;
    }

    cs_insn *ins;
    size_t avail = (size_t)fsz - off;
    /* luaL_openlibs is ~0xc2 bytes / ~60 instructions; decode 80 to be safe. */
    size_t n = cs_disasm(h, buf + off, avail, rva, 80, &ins);
    if (n == 0) {
        printf("[disasm_check] FAIL: no instructions decoded at RVA 0x%x\n", rva);
        cs_close(&h); free(buf); return 1;
    }

    openlibs_features_t feat; memset(&feat, 0, sizeof(feat));
    feat.all_calls_in_cluster = 1;   /* optimistic; cleared on first miss */
    uint32_t call_targets[16]; int call_targets_n = 0;

    for (size_t i = 0; i < n; i++) {
        classify_openlibs_insn(&ins[i], &feat,
                                call_targets, &call_targets_n, 16);
        /* Stop at the function boundary: RET (regular return) or a direct
         * unconditional JMP (tail call — luaL_openlibs ends with
         * `jmp lua_settop`). Either marks the end of the function body. */
        if (ins[i].id == X86_INS_RET) break;
        if (ins[i].id == X86_INS_JMP) {
            feat.body_end_rva = (uint32_t)(ins[i].address + ins[i].size);
            break;
        }
    }
    feat.n_distinct_call_tgts = call_targets_n;
    /* body_size = end-of-last-insn minus function start. */
    uint32_t body_size = feat.body_end_rva - rva;

    /* Report features. */
    printf("[disasm_check] decoded %zu insns at RVA 0x%x (file off 0x%x)\n",
           n, rva, off);
    printf("[disasm_check] openlibs features:"
           " preload_lea=%d direct_calls=%d distinct_tgts=%d"
           " backward_jne=%d all_in_cluster=%d body_size=0x%x\n",
           feat.has_preload_lea, feat.n_direct_calls, feat.n_distinct_call_tgts,
           feat.n_backward_jne, feat.all_calls_in_cluster, body_size);

    /* Required features for a match. */
    int ok = feat.has_preload_lea &&
             feat.n_direct_calls >= 5 &&
             feat.n_distinct_call_tgts >= 5 &&
             feat.n_backward_jne >= 1 &&
             feat.all_calls_in_cluster &&
             body_size <= 0x200;

    if (!ok) {
        printf("[disasm_check] FAIL: shape does not match luaL_openlibs\n");
        printf("[disasm_check] --- disassembly (first 40 insns) ---\n");
        size_t lim = n < 40 ? n : 40;
        for (size_t i = 0; i < lim; i++)
            printf("[disasm_check]   0x%06llx: %-8s %s\n",
                   (unsigned long long)ins[i].address,
                   ins[i].mnemonic, ins[i].op_str);
        printf("[disasm_check] --- end disassembly ---\n");
    } else {
        printf("[disasm_check] OK: shape matches luaL_openlibs @ RVA 0x%x\n", rva);
    }

    cs_free(ins, n);
    cs_close(&h);
    free(buf);
    return ok ? 0 : 1;
}

/* =====================================================================*
 *  lua_pushcclosure shape matcher (Phase 5 Step 3 — C-function bootstrap)
 * =====================================================================*
 * Verify the function at `rva` has lua_pushcclosure's compiled shape.
 * Implementation reuses the same PE+capstone plumbing.
 *
 * LuaJIT 2.1 lj_api.c:678 source structure:
 *   LUA_API void lua_pushcclosure(lua_State *L, lua_CFunction f, int n) {
 *     GCfunc *fn;
 *     lj_gc_check(L);                              // conditional call
 *     lj_checkapi_slot(n);
 *     fn = lj_func_newC(L, (MSize)n, getcurrenv(L));   // call
 *     fn->c.f = f;
 *     L->top -= n;
 *     while (n--) copyTV(L, &fn->c.upvalue[n], L->top+n);  // backward jne
 *     setfuncV(L, L->top, fn);                     // writes 0xfffffff7 tag
 *     incr_top(L);                                 // bounds check + call
 *   }
 *
 * Distinctive compiled features (verified against the analyzed binary):
 *   - `movsxd rdi, r8d` (or similar) ; sign-extend n arg
 *   - At least one backward `jne`     ; the upvalue-copy loop
 *   - Writes `0xfffffff7` constant    ; LJ_TFUNC tag (low byte 0xF7)
 *   - Exactly 3 direct `call rel32`   ; lj_gc_check, lj_func_newC,
 *                                      ; lj_state_growstack
 *   - Body <= 0x100 bytes
 *
 * Unique among 219 cluster candidates ([0xc73000, 0xc80000) prologue starts).
 */

typedef struct {
    int has_movsxd_r8d;       /* movsxd of r8d (sign-extend nups) */
    int n_backward_jne;       /* backward `jne` (upvalue-copy loop) */
    int has_func_tag;         /* writes 0xfffffff7 (LJ_TFUNC) */
    int n_direct_calls;       /* count of `call rel32` */
    int all_calls_in_cluster; /* all calls land in [0xc70000, 0xc90000) */
    uint32_t body_end_rva;
} pushcclosure_features_t;

static void classify_pushcclosure_insn(const cs_insn *ins,
                                        pushcclosure_features_t *feat) {
    feat->body_end_rva = (uint32_t)(ins->address + ins->size);

    /* movsxd rdi/rax/etc, r8d — sign-extend the nups arg. */
    if (ins->id == X86_INS_MOVSXD) {
        const char *o = ins->op_str;
        if (strstr(o, "r8d") || strstr(o, "r8b")) feat->has_movsxd_r8d = 1;
    }

    /* backward jne = loop back-edge (the while(n--) copyTV loop). */
    if (ins->id == X86_INS_JNE) {
        unsigned long long tgt = strtoull(ins->op_str, NULL, 0);
        if (tgt < ins->address) feat->n_backward_jne++;
    }

    /* LJ_TFUNC tag = 0xfffffff7 (low byte 0xF7). Written by setfuncV. */
    if (strstr(ins->op_str, "0xfffffff7")) feat->has_func_tag = 1;

    /* Direct call rel32 — capstone prints the absolute target. */
    if (ins->id == X86_INS_CALL && ins->op_str[0] >= '0' && ins->op_str[0] <= '9') {
        feat->n_direct_calls++;
        unsigned long long tgt = strtoull(ins->op_str, NULL, 0);
        if (!(tgt >= 0xc70000 && tgt < 0xc90000)) feat->all_calls_in_cluster = 0;
    }
}

int disasm_check_lua_pushcclosure(const char *pe_path, uint32_t rva) {
    const char *path = pe_path ? pe_path : DEFAULT_PE_PATH;
    FILE *fp = fopen(path, "rb");
    if (!fp) {
        printf("[disasm_check] FAIL: cannot open %s (%s)\n",
               path, strerror(errno));
        return 2;
    }
    fseek(fp, 0, SEEK_END);
    long fsz = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    if (fsz <= 0) { fclose(fp); return 2; }
    uint8_t *buf = (uint8_t *)malloc((size_t)fsz);
    if (!buf) { fclose(fp); return 2; }
    if (fread(buf, 1, (size_t)fsz, fp) != (size_t)fsz) {
        printf("[disasm_check] FAIL: short read on %s\n", path);
        fclose(fp); free(buf); return 2;
    }
    fclose(fp);

    uint32_t off = 0;
    if (pe_rva_to_off(buf, (size_t)fsz, rva, &off) != 0) {
        printf("[disasm_check] FAIL: RVA 0x%x not in any section of %s\n",
               rva, path);
        free(buf); return 1;
    }

    csh h;
    if (cs_open(CS_ARCH_X86, CS_MODE_64, &h) != CS_ERR_OK) {
        printf("[disasm_check] FAIL: cs_open failed\n");
        free(buf); return 2;
    }

    cs_insn *ins;
    size_t avail = (size_t)fsz - off;
    /* lua_pushcclosure is ~0xae bytes / ~50 instructions; decode 64 to be safe. */
    size_t n = cs_disasm(h, buf + off, avail, rva, 64, &ins);
    if (n == 0) {
        printf("[disasm_check] FAIL: no instructions decoded at RVA 0x%x\n", rva);
        cs_close(&h); free(buf); return 1;
    }

    pushcclosure_features_t feat; memset(&feat, 0, sizeof(feat));
    feat.all_calls_in_cluster = 1;   /* optimistic; cleared on first miss */

    for (size_t i = 0; i < n; i++) {
        classify_pushcclosure_insn(&ins[i], &feat);
        /* Stop at function boundary: RET (regular return), or a JMP that
         * looks like a tail-call (target outside this function body).
         * Intra-body forward jmps (e.g. over the `getcurrenv` conditional
         * at 0xc745bb: jmp 0xc745c0) must NOT trigger the stop — only a
         * JMP to an address before the function start or more than 0x100
         * bytes ahead is treated as a tail-call. */
        if (ins[i].id == X86_INS_RET) break;
        if (ins[i].id == X86_INS_JMP) {
            unsigned long long tgt = strtoull(ins[i].op_str, NULL, 0);
            if (tgt < rva || tgt > rva + 0x100) {
                feat.body_end_rva = (uint32_t)(ins[i].address + ins[i].size);
                break;
            }
        }
    }
    uint32_t body_size = feat.body_end_rva - rva;

    printf("[disasm_check] decoded %zu insns at RVA 0x%x (file off 0x%x)\n",
           n, rva, off);
    printf("[disasm_check] pushcclosure features:"
           " movsxd_r8d=%d backward_jne=%d func_tag(0xF7)=%d"
           " direct_calls=%d all_in_cluster=%d body_size=0x%x\n",
           feat.has_movsxd_r8d, feat.n_backward_jne, feat.has_func_tag,
           feat.n_direct_calls, feat.all_calls_in_cluster, body_size);

    int ok = feat.has_movsxd_r8d &&
             feat.n_backward_jne >= 1 &&
             feat.has_func_tag &&
             feat.n_direct_calls == 3 &&
             feat.all_calls_in_cluster &&
             body_size <= 0x100;

    if (!ok) {
        printf("[disasm_check] FAIL: shape does not match lua_pushcclosure\n");
        printf("[disasm_check] --- disassembly (first 40 insns) ---\n");
        size_t lim = n < 40 ? n : 40;
        for (size_t i = 0; i < lim; i++)
            printf("[disasm_check]   0x%06llx: %-8s %s\n",
                   (unsigned long long)ins[i].address,
                   ins[i].mnemonic, ins[i].op_str);
        printf("[disasm_check] --- end disassembly ---\n");
    } else {
        printf("[disasm_check] OK: shape matches lua_pushcclosure @ RVA 0x%x\n", rva);
    }

    cs_free(ins, n);
    cs_close(&h);
    free(buf);
    return ok ? 0 : 1;
}

/* =====================================================================*
 *  Phase 5 — DMF bootstrap matchers
 * =====================================================================*
 * Same PE+capstone plumbing as the Phase 4 matchers above. Each function
 * has a source-derived feature set that is unique in the LuaJIT API
 * cluster — verified by scanning all candidate function starts in
 * [0xc73000, 0xc90000).
 */

/* ---- lua_tolstring (Phase 5) ----------------------------------------- */

typedef struct {
    int has_index2adr_call;       /* call 0xc72be0 */
    int n_index2adr_calls;
    int has_gc_check_call;        /* call 0xc82fc0 (lj_gc_check) */
    int has_strfmt_call;          /* call 0xc89700 (lj_strfmt_number) */
    int has_strdata_add_0x14;     /* `add rax, 0x14` near the end (strdata(s)) */
    int has_len_store_at_0x10;    /* `mov [reg+0x10], ...` (*len = s->len) */
    int has_len_null_test;        /* NULL check on len (test rdi,rdi or test r8,r8) */
    int n_direct_calls;
    int all_calls_in_cluster;
    uint32_t body_end_rva;
} tolstring_features_t;

static void classify_tolstring(const cs_insn *ins, tolstring_features_t *feat) {
    feat->body_end_rva = (uint32_t)(ins->address + ins->size);

    if (ins->id == X86_INS_CALL && ins->op_str[0] >= '0' && ins->op_str[0] <= '9') {
        feat->n_direct_calls++;
        unsigned long long tgt = strtoull(ins->op_str, NULL, 0);
        if (tgt == 0xc72be0) {
            feat->has_index2adr_call = 1;
            feat->n_index2adr_calls++;
        }
        if (tgt == 0xc82fc0) feat->has_gc_check_call = 1;
        if (tgt == 0xc89700) feat->has_strfmt_call = 1;
        if (!(tgt >= 0xc70000 && tgt < 0xcb0000))
            feat->all_calls_in_cluster = 0;
    }

    /* `add rax, 0x14` near the end (strdata computation). */
    if (ins->id == X86_INS_ADD && strstr(ins->op_str, "rax, 0x14"))
        feat->has_strdata_add_0x14 = 1;

    /* *len = s->len store: `mov [reg+0x10], ...` (s->len at offset 0x10). */
    if (ins->id == X86_INS_MOV && strstr(ins->op_str, "+ 0x10]"))
        feat->has_len_store_at_0x10 = 1;

    /* NULL check on len (test rdi,rdi or test r8,r8). */
    if (ins->id == X86_INS_TEST) {
        const char *o = ins->op_str;
        if (strstr(o, "rdi") || strstr(o, "r8")) feat->has_len_null_test = 1;
    }
}

int disasm_check_lua_tolstring(const char *pe_path, uint32_t rva) {
    const char *path = pe_path ? pe_path : DEFAULT_PE_PATH;
    FILE *fp = fopen(path, "rb");
    if (!fp) {
        printf("[disasm_check] FAIL: cannot open %s (%s)\n", path, strerror(errno));
        return 2;
    }
    fseek(fp, 0, SEEK_END);
    long fsz = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    if (fsz <= 0) { fclose(fp); return 2; }
    uint8_t *buf = (uint8_t *)malloc((size_t)fsz);
    if (!buf) { fclose(fp); return 2; }
    if (fread(buf, 1, (size_t)fsz, fp) != (size_t)fsz) {
        fclose(fp); free(buf); return 2;
    }
    fclose(fp);

    uint32_t off = 0;
    if (pe_rva_to_off(buf, (size_t)fsz, rva, &off) != 0) {
        printf("[disasm_check] FAIL: RVA 0x%x not in any section of %s\n", rva, path);
        free(buf); return 1;
    }

    csh h;
    if (cs_open(CS_ARCH_X86, CS_MODE_64, &h) != CS_ERR_OK) {
        printf("[disasm_check] FAIL: cs_open failed\n");
        free(buf); return 2;
    }

    cs_insn *ins;
    size_t avail = (size_t)fsz - off;
    size_t n = cs_disasm(h, buf + off, avail, rva, 64, &ins);
    if (n == 0) {
        printf("[disasm_check] FAIL: no instructions decoded at RVA 0x%x\n", rva);
        cs_close(&h); free(buf); return 1;
    }

    tolstring_features_t feat; memset(&feat, 0, sizeof(feat));
    feat.all_calls_in_cluster = 1;

    for (size_t i = 0; i < n; i++) {
        classify_tolstring(&ins[i], &feat);
        if (ins[i].id == X86_INS_RET) break;
        if (ins[i].id == X86_INS_JMP) {
            unsigned long long tgt = strtoull(ins[i].op_str, NULL, 0);
            if (tgt < rva || tgt > rva + 0x200) {
                feat.body_end_rva = (uint32_t)(ins[i].address + ins[i].size);
                break;
            }
        }
    }
    uint32_t body_size = feat.body_end_rva - rva;

    printf("[disasm_check] decoded %zu insns at RVA 0x%x (file off 0x%x)\n",
           n, rva, off);
    printf("[disasm_check] tolstring features:"
           " idx2adr=%d(n=%d) gc_check=%d strfmt=%d strdata(+0x14)=%d"
           " len_store(+0x10)=%d len_null_test=%d"
           " direct_calls=%d all_in_cluster=%d body_size=0x%x\n",
           feat.has_index2adr_call, feat.n_index2adr_calls,
           feat.has_gc_check_call, feat.has_strfmt_call,
           feat.has_strdata_add_0x14, feat.has_len_store_at_0x10,
           feat.has_len_null_test,
           feat.n_direct_calls, feat.all_calls_in_cluster, body_size);

    int ok = feat.has_index2adr_call &&
             feat.has_strdata_add_0x14 &&
             feat.has_len_store_at_0x10 &&
             feat.has_len_null_test &&
             feat.n_direct_calls >= 1 && feat.n_direct_calls <= 5 &&
             feat.all_calls_in_cluster &&
             body_size >= 0x40 && body_size <= 0x150;

    if (!ok) {
        printf("[disasm_check] FAIL: shape does not match lua_tolstring\n");
        printf("[disasm_check] --- disassembly (first 40 insns) ---\n");
        size_t lim = n < 40 ? n : 40;
        for (size_t i = 0; i < lim; i++)
            printf("[disasm_check]   0x%06llx: %-8s %s\n",
                   (unsigned long long)ins[i].address,
                   ins[i].mnemonic, ins[i].op_str);
        printf("[disasm_check] --- end disassembly ---\n");
    } else {
        printf("[disasm_check] OK: shape matches lua_tolstring @ RVA 0x%x\n", rva);
    }

    cs_free(ins, n);
    cs_close(&h);
    free(buf);
    return ok ? 0 : 1;
}

/* ---- lua_createtable (Phase 5) --------------------------------------- */

typedef struct {
    int has_tab_tag;             /* writes 0xfffffff4 (LJ_TTAB) */
    int has_top_increment;       /* `add [reg+0x18], 8` (incr_top) */
    int has_gc_check_call;       /* call 0xc82fc0 */
    int has_tab_new_call;        /* call 0xc84510 (lj_tab_new_ah) */
    int has_growstack_call;      /* call 0xc7ede0 (lj_state_growstack) */
    int n_direct_calls;
    int all_calls_in_cluster;
    uint32_t body_end_rva;
} createtable_features_t;

static void classify_createtable(const cs_insn *ins, createtable_features_t *feat) {
    feat->body_end_rva = (uint32_t)(ins->address + ins->size);

    /* LJ_TTAB tag = 0xfffffff4 (low byte 0xF4). */
    if (strstr(ins->op_str, "0xfffffff4")) feat->has_tab_tag = 1;

    /* incr_top: `add qword ptr [reg + 0x18], 8`. */
    if (ins->id == X86_INS_ADD &&
        strstr(ins->op_str, "+ 0x18]") &&
        (strstr(ins->op_str, ", 8") || strstr(ins->op_str, ", 0x8"))) {
        feat->has_top_increment = 1;
    }

    if (ins->id == X86_INS_CALL && ins->op_str[0] >= '0' && ins->op_str[0] <= '9') {
        feat->n_direct_calls++;
        unsigned long long tgt = strtoull(ins->op_str, NULL, 0);
        if (tgt == 0xc82fc0) feat->has_gc_check_call = 1;
        if (tgt == 0xc84510) feat->has_tab_new_call = 1;
        if (tgt == 0xc7ede0) feat->has_growstack_call = 1;
        if (!(tgt >= 0xc70000 && tgt < 0xc90000))
            feat->all_calls_in_cluster = 0;
    }
}

int disasm_check_lua_createtable(const char *pe_path, uint32_t rva) {
    const char *path = pe_path ? pe_path : DEFAULT_PE_PATH;
    FILE *fp = fopen(path, "rb");
    if (!fp) {
        printf("[disasm_check] FAIL: cannot open %s (%s)\n", path, strerror(errno));
        return 2;
    }
    fseek(fp, 0, SEEK_END);
    long fsz = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    if (fsz <= 0) { fclose(fp); return 2; }
    uint8_t *buf = (uint8_t *)malloc((size_t)fsz);
    if (!buf) { fclose(fp); return 2; }
    if (fread(buf, 1, (size_t)fsz, fp) != (size_t)fsz) {
        fclose(fp); free(buf); return 2;
    }
    fclose(fp);

    uint32_t off = 0;
    if (pe_rva_to_off(buf, (size_t)fsz, rva, &off) != 0) {
        printf("[disasm_check] FAIL: RVA 0x%x not in any section of %s\n", rva, path);
        free(buf); return 1;
    }

    csh h;
    if (cs_open(CS_ARCH_X86, CS_MODE_64, &h) != CS_ERR_OK) {
        printf("[disasm_check] FAIL: cs_open failed\n");
        free(buf); return 2;
    }

    cs_insn *ins;
    size_t avail = (size_t)fsz - off;
    size_t n = cs_disasm(h, buf + off, avail, rva, 40, &ins);
    if (n == 0) {
        printf("[disasm_check] FAIL: no instructions decoded at RVA 0x%x\n", rva);
        cs_close(&h); free(buf); return 1;
    }

    createtable_features_t feat; memset(&feat, 0, sizeof(feat));
    feat.all_calls_in_cluster = 1;

    for (size_t i = 0; i < n; i++) {
        classify_createtable(&ins[i], &feat);
        if (ins[i].id == X86_INS_RET) break;
        if (ins[i].id == X86_INS_JMP) {
            unsigned long long tgt = strtoull(ins[i].op_str, NULL, 0);
            if (tgt < rva || tgt > rva + 0x100) {
                feat.body_end_rva = (uint32_t)(ins[i].address + ins[i].size);
                break;
            }
        }
    }
    uint32_t body_size = feat.body_end_rva - rva;

    printf("[disasm_check] decoded %zu insns at RVA 0x%x (file off 0x%x)\n",
           n, rva, off);
    printf("[disasm_check] createtable features:"
           " tab_tag(0xF4)=%d top_increment=%d gc_check=%d tab_new=%d"
           " growstack=%d direct_calls=%d all_in_cluster=%d body_size=0x%x\n",
           feat.has_tab_tag, feat.has_top_increment, feat.has_gc_check_call,
           feat.has_tab_new_call, feat.has_growstack_call, feat.n_direct_calls,
           feat.all_calls_in_cluster, body_size);

    int ok = feat.has_tab_tag &&
             feat.has_top_increment &&
             feat.has_tab_new_call &&
             feat.n_direct_calls >= 2 && feat.n_direct_calls <= 4 &&
             feat.all_calls_in_cluster &&
             body_size <= 0x90;

    if (!ok) {
        printf("[disasm_check] FAIL: shape does not match lua_createtable\n");
        printf("[disasm_check] --- disassembly (first 40 insns) ---\n");
        size_t lim = n < 40 ? n : 40;
        for (size_t i = 0; i < lim; i++)
            printf("[disasm_check]   0x%06llx: %-8s %s\n",
                   (unsigned long long)ins[i].address,
                   ins[i].mnemonic, ins[i].op_str);
        printf("[disasm_check] --- end disassembly ---\n");
    } else {
        printf("[disasm_check] OK: shape matches lua_createtable @ RVA 0x%x\n", rva);
    }

    cs_free(ins, n);
    cs_close(&h);
    free(buf);
    return ok ? 0 : 1;
}

/* ---- lua_type (Phase 5) ---------------------------------------------- */

typedef struct {
    int has_index2adr_call;
    int has_magic_constant;        /* movabs rax, 0x75a0698042110 */
    int has_shift_by_cl;
    int has_and_0xf;
    int n_direct_calls;
    uint32_t body_end_rva;
} lua_type_features_t;

static void classify_lua_type(const cs_insn *ins, lua_type_features_t *feat) {
    feat->body_end_rva = (uint32_t)(ins->address + ins->size);

    if (ins->id == X86_INS_CALL && ins->op_str[0] >= '0' && ins->op_str[0] <= '9') {
        feat->n_direct_calls++;
        unsigned long long tgt = strtoull(ins->op_str, NULL, 0);
        if (tgt == 0xc72be0) feat->has_index2adr_call = 1;
    }

    /* movabs rax, 0x75a0698042110 — the magic type lookup constant. */
    if (ins->id == X86_INS_MOVABS && strstr(ins->op_str, "0x75a0698042110"))
        feat->has_magic_constant = 1;

    if (ins->id == X86_INS_SHR && strstr(ins->op_str, ", cl"))
        feat->has_shift_by_cl = 1;

    if (ins->id == X86_INS_AND && strstr(ins->op_str, ", 0xf"))
        feat->has_and_0xf = 1;
}

int disasm_check_lua_type(const char *pe_path, uint32_t rva) {
    const char *path = pe_path ? pe_path : DEFAULT_PE_PATH;
    FILE *fp = fopen(path, "rb");
    if (!fp) {
        printf("[disasm_check] FAIL: cannot open %s (%s)\n", path, strerror(errno));
        return 2;
    }
    fseek(fp, 0, SEEK_END);
    long fsz = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    if (fsz <= 0) { fclose(fp); return 2; }
    uint8_t *buf = (uint8_t *)malloc((size_t)fsz);
    if (!buf) { fclose(fp); return 2; }
    if (fread(buf, 1, (size_t)fsz, fp) != (size_t)fsz) {
        fclose(fp); free(buf); return 2;
    }
    fclose(fp);

    uint32_t off = 0;
    if (pe_rva_to_off(buf, (size_t)fsz, rva, &off) != 0) {
        printf("[disasm_check] FAIL: RVA 0x%x not in any section of %s\n", rva, path);
        free(buf); return 1;
    }

    csh h;
    if (cs_open(CS_ARCH_X86, CS_MODE_64, &h) != CS_ERR_OK) {
        printf("[disasm_check] FAIL: cs_open failed\n");
        free(buf); return 2;
    }

    cs_insn *ins;
    size_t avail = (size_t)fsz - off;
    size_t n = cs_disasm(h, buf + off, avail, rva, 48, &ins);
    if (n == 0) {
        printf("[disasm_check] FAIL: no instructions decoded at RVA 0x%x\n", rva);
        cs_close(&h); free(buf); return 1;
    }

    lua_type_features_t feat; memset(&feat, 0, sizeof(feat));

    /* lua_type has multiple early-return paths (return LUA_TNUMBER,
     * return LUA_TLIGHTUSERDATA, return LUA_TNONE, fall-through to
     * magic-constant path). Don't stop at the first ret — scan all
     * decoded instructions until int3 padding. */
    for (size_t i = 0; i < n; i++) {
        classify_lua_type(&ins[i], &feat);
        if (ins[i].id == X86_INS_INT3) {
            feat.body_end_rva = (uint32_t)ins[i].address;
            break;
        }
        if (ins[i].id == X86_INS_JMP) {
            unsigned long long tgt = strtoull(ins[i].op_str, NULL, 0);
            if (tgt < rva || tgt > rva + 0x100) {
                feat.body_end_rva = (uint32_t)(ins[i].address + ins[i].size);
                break;
            }
        }
        feat.body_end_rva = (uint32_t)(ins[i].address + ins[i].size);
    }
    uint32_t body_size = feat.body_end_rva - rva;

    printf("[disasm_check] decoded %zu insns at RVA 0x%x (file off 0x%x)\n",
           n, rva, off);
    printf("[disasm_check] lua_type features:"
           " idx2adr=%d magic_const=%d shift_cl=%d and_0xf=%d"
           " direct_calls=%d body_size=0x%x\n",
           feat.has_index2adr_call, feat.has_magic_constant, feat.has_shift_by_cl,
           feat.has_and_0xf, feat.n_direct_calls, body_size);

    /* The magic constant is the key discriminator — it's used nowhere else
     * in the cluster (verified: only 2 occurrences in the whole binary,
     * the other being an inline expansion inside lj_meta_comp). */
    int ok = feat.has_index2adr_call &&
             feat.has_magic_constant &&
             feat.n_direct_calls == 1;

    if (!ok) {
        printf("[disasm_check] FAIL: shape does not match lua_type\n");
        printf("[disasm_check] --- disassembly (first 40 insns) ---\n");
        size_t lim = n < 40 ? n : 40;
        for (size_t i = 0; i < lim; i++)
            printf("[disasm_check]   0x%06llx: %-8s %s\n",
                   (unsigned long long)ins[i].address,
                   ins[i].mnemonic, ins[i].op_str);
        printf("[disasm_check] --- end disassembly ---\n");
    } else {
        printf("[disasm_check] OK: shape matches lua_type @ RVA 0x%x\n", rva);
    }

    cs_free(ins, n);
    cs_close(&h);
    free(buf);
    return ok ? 0 : 1;
}

/* ---- lua_tonumber (Phase 5) ------------------------------------------ */

typedef struct {
    int has_index2adr_call;
    int has_strscan_call;          /* call 0xc886e0 (lj_strscan_num) */
    int has_movsd_xmm0;
    int has_number_tag_check;      /* cmp [..], 0xfffeffff */
    int has_str_tag_check;         /* cmp [..], 0xfffffffb */
    int n_direct_calls;
    int all_calls_in_cluster;
    uint32_t body_end_rva;
} tonumber_features_t;

static void classify_tonumber(const cs_insn *ins, tonumber_features_t *feat) {
    feat->body_end_rva = (uint32_t)(ins->address + ins->size);

    if (ins->id == X86_INS_CALL && ins->op_str[0] >= '0' && ins->op_str[0] <= '9') {
        feat->n_direct_calls++;
        unsigned long long tgt = strtoull(ins->op_str, NULL, 0);
        if (tgt == 0xc72be0) feat->has_index2adr_call = 1;
        if (tgt == 0xc886e0) feat->has_strscan_call = 1;
        if (!(tgt >= 0xc70000 && tgt < 0xcb0000))
            feat->all_calls_in_cluster = 0;
    }

    if (ins->id == X86_INS_MOVSD && strstr(ins->op_str, "xmm0"))
        feat->has_movsd_xmm0 = 1;

    if (ins->id == X86_INS_CMP && strstr(ins->op_str, "0xfffeffff"))
        feat->has_number_tag_check = 1;
    /* LJ_TSTR = 0xfffffffb; capstone prints this as either "0xfffffffb"
     * OR the signed-decimal form "-5" (for 32-bit operands). */
    if (ins->id == X86_INS_CMP &&
        (strstr(ins->op_str, "0xfffffffb") || strstr(ins->op_str, ", -5")))
        feat->has_str_tag_check = 1;
}

int disasm_check_lua_tonumber(const char *pe_path, uint32_t rva) {
    const char *path = pe_path ? pe_path : DEFAULT_PE_PATH;
    FILE *fp = fopen(path, "rb");
    if (!fp) {
        printf("[disasm_check] FAIL: cannot open %s (%s)\n", path, strerror(errno));
        return 2;
    }
    fseek(fp, 0, SEEK_END);
    long fsz = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    if (fsz <= 0) { fclose(fp); return 2; }
    uint8_t *buf = (uint8_t *)malloc((size_t)fsz);
    if (!buf) { fclose(fp); return 2; }
    if (fread(buf, 1, (size_t)fsz, fp) != (size_t)fsz) {
        fclose(fp); free(buf); return 2;
    }
    fclose(fp);

    uint32_t off = 0;
    if (pe_rva_to_off(buf, (size_t)fsz, rva, &off) != 0) {
        printf("[disasm_check] FAIL: RVA 0x%x not in any section of %s\n", rva, path);
        free(buf); return 1;
    }

    csh h;
    if (cs_open(CS_ARCH_X86, CS_MODE_64, &h) != CS_ERR_OK) {
        printf("[disasm_check] FAIL: cs_open failed\n");
        free(buf); return 2;
    }

    cs_insn *ins;
    size_t avail = (size_t)fsz - off;
    size_t n = cs_disasm(h, buf + off, avail, rva, 48, &ins);
    if (n == 0) {
        printf("[disasm_check] FAIL: no instructions decoded at RVA 0x%x\n", rva);
        cs_close(&h); free(buf); return 1;
    }

    tonumber_features_t feat; memset(&feat, 0, sizeof(feat));
    feat.all_calls_in_cluster = 1;

    /* lua_tonumber has multiple return points (early return on number,
     * return after strscan, fall-through to error path). Don't stop at
     * the first ret — scan all decoded instructions until int3 padding. */
    for (size_t i = 0; i < n; i++) {
        classify_tonumber(&ins[i], &feat);
        /* Stop on int3 padding (real function boundary). */
        if (ins[i].id == X86_INS_INT3) {
            feat.body_end_rva = (uint32_t)ins[i].address;
            break;
        }
        if (ins[i].id == X86_INS_JMP) {
            unsigned long long tgt = strtoull(ins[i].op_str, NULL, 0);
            if (tgt < rva || tgt > rva + 0x100) {
                feat.body_end_rva = (uint32_t)(ins[i].address + ins[i].size);
                break;
            }
        }
        /* Keep updating body_end on every insn (in case we never hit int3). */
        feat.body_end_rva = (uint32_t)(ins[i].address + ins[i].size);
    }
    uint32_t body_size = feat.body_end_rva - rva;

    printf("[disasm_check] decoded %zu insns at RVA 0x%x (file off 0x%x)\n",
           n, rva, off);
    printf("[disasm_check] tonumber features:"
           " idx2adr=%d strscan=%d movsd_xmm0=%d num_tag=%d str_tag=%d"
           " direct_calls=%d all_in_cluster=%d body_size=0x%x\n",
           feat.has_index2adr_call, feat.has_strscan_call, feat.has_movsd_xmm0,
           feat.has_number_tag_check, feat.has_str_tag_check, feat.n_direct_calls,
           feat.all_calls_in_cluster, body_size);

    int ok = feat.has_index2adr_call &&
             feat.has_movsd_xmm0 &&
             feat.has_number_tag_check &&
             feat.has_str_tag_check &&
             feat.has_strscan_call &&
             feat.n_direct_calls >= 1 && feat.n_direct_calls <= 3 &&
             feat.all_calls_in_cluster &&
             body_size <= 0x90;

    if (!ok) {
        printf("[disasm_check] FAIL: shape does not match lua_tonumber\n");
        printf("[disasm_check] --- disassembly (first 40 insns) ---\n");
        size_t lim = n < 40 ? n : 40;
        for (size_t i = 0; i < lim; i++)
            printf("[disasm_check]   0x%06llx: %-8s %s\n",
                   (unsigned long long)ins[i].address,
                   ins[i].mnemonic, ins[i].op_str);
        printf("[disasm_check] --- end disassembly ---\n");
    } else {
        printf("[disasm_check] OK: shape matches lua_tonumber @ RVA 0x%x\n", rva);
    }

    cs_free(ins, n);
    cs_close(&h);
    free(buf);
    return ok ? 0 : 1;
}

/* =====================================================================*
 *  lua_setfield shape matcher (Phase 5 Step 3 — C-function bootstrap)
 * =====================================================================*
 * Verify the function at `rva` has lua_setfield's compiled shape.
 *
 * LuaJIT 2.1 lj_api.c:970 source structure (3 args: L, idx, k):
 *   LUA_API void lua_setfield(lua_State *L, int idx, const char *k) {
 *     TValue *t = index2adr(L, idx);          // first call; edx still = idx
 *     ... keyinit / lj_str_newz(L, k) ...     // second call
 *     o = lj_tab_set(L, t, &key);             // third call
 *     copyTV(L, o, L->top - 1);
 *     L->top--;                               // lea rcx,[rdx-8]; mov [rsi+0x18],rcx
 *   }
 *
 * Distinctive compiled features:
 *   - 3-arg prologue: `mov rsi, rcx` (save L) AND `mov rbx, r8` (save k);
 *     does NOT save `edx` (idx — consumed by the first call to index2adr)
 *   - Writes `0xfffffffb` (LJ_TSTR tag for the key TValue)
 *   - Ends with `lea rcx, [rdx - 8]; mov [rsi + 0x18], rcx` (L->top--)
 *   - Exactly 4 direct `call rel32` (index2adr, lj_str_newz, lj_tab_setkey,
 *     lj_tab_set)
 *   - Body <= 0x100 bytes
 *
 * Unique among 219 cluster candidates.
 */

typedef struct {
    int has_save_rsi_from_rcx;  /* mov rsi, rcx — save L */
    int has_save_rbx_from_r8;   /* mov rbx, r8  — save k */
    int has_str_tag;            /* writes 0xfffffffb (LJ_TSTR key tag) */
    int has_top_decrement;      /* ends with lea rcx,[rdx-8]; mov [rsi+0x18],rcx */
    int n_direct_calls;
    int all_calls_in_cluster;
    uint32_t body_end_rva;
} setfield_features_t;

static void classify_setfield_insn(const cs_insn *ins, size_t insn_index,
                                    const cs_insn *insns, size_t n_insns,
                                    setfield_features_t *feat) {
    feat->body_end_rva = (uint32_t)(ins->address + ins->size);

    /* Prologue saves: only count in the first 8 instructions (prologue window). */
    if (insn_index < 8) {
        if (ins->id == X86_INS_MOV && strcmp(ins->op_str, "rsi, rcx") == 0)
            feat->has_save_rsi_from_rcx = 1;
        if (ins->id == X86_INS_MOV && strcmp(ins->op_str, "rbx, r8") == 0)
            feat->has_save_rbx_from_r8 = 1;
    }

    /* LJ_TSTR key tag = 0xfffffffb (low byte 0xFB). Written during keyinit. */
    if (strstr(ins->op_str, "0xfffffffb")) feat->has_str_tag = 1;

    /* L->top-- cleanup: lea rcx, [rdx - 8] followed by mov [rsi + 0x18], rcx.
     * Capstone formats displacements as decimal under 10 and hex above. */
    if (ins->id == X86_INS_LEA && insn_index + 1 < n_insns) {
        const char *o = ins->op_str;
        /* Match "rcx, [rdx - 8]" or "rcx, [rdx - 0x8]". */
        if (strstr(o, "rcx") && (strstr(o, "[rdx - 8]") ||
                                  strstr(o, "[rdx - 0x8]"))) {
            const cs_insn *nxt = &insns[insn_index + 1];
            if (nxt->id == X86_INS_MOV && strstr(nxt->op_str, "[rsi + 0x18]"))
                feat->has_top_decrement = 1;
        }
    }

    /* Direct call rel32. Allow the 0xdf5xxx lj_str_newz fast-path too
     * (setfield calls into the string-intern cluster at 0xdf5a98). */
    if (ins->id == X86_INS_CALL && ins->op_str[0] >= '0' && ins->op_str[0] <= '9') {
        feat->n_direct_calls++;
        unsigned long long tgt = strtoull(ins->op_str, NULL, 0);
        /* 0xc70000-0xc90000 = LuaJIT API cluster; 0xdf5000-0xe00000 = string-intern cluster. */
        if (!(tgt >= 0xc70000 && tgt < 0xc90000) && !(tgt >= 0xdf5000ULL && tgt < 0xe00000ULL))
            feat->all_calls_in_cluster = 0;
    }
}

int disasm_check_lua_setfield(const char *pe_path, uint32_t rva) {
    const char *path = pe_path ? pe_path : DEFAULT_PE_PATH;
    FILE *fp = fopen(path, "rb");
    if (!fp) {
        printf("[disasm_check] FAIL: cannot open %s (%s)\n",
               path, strerror(errno));
        return 2;
    }
    fseek(fp, 0, SEEK_END);
    long fsz = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    if (fsz <= 0) { fclose(fp); return 2; }
    uint8_t *buf = (uint8_t *)malloc((size_t)fsz);
    if (!buf) { fclose(fp); return 2; }
    if (fread(buf, 1, (size_t)fsz, fp) != (size_t)fsz) {
        printf("[disasm_check] FAIL: short read on %s\n", path);
        fclose(fp); free(buf); return 2;
    }
    fclose(fp);

    uint32_t off = 0;
    if (pe_rva_to_off(buf, (size_t)fsz, rva, &off) != 0) {
        printf("[disasm_check] FAIL: RVA 0x%x not in any section of %s\n",
               rva, path);
        free(buf); return 1;
    }

    csh h;
    if (cs_open(CS_ARCH_X86, CS_MODE_64, &h) != CS_ERR_OK) {
        printf("[disasm_check] FAIL: cs_open failed\n");
        free(buf); return 2;
    }

    cs_insn *ins;
    size_t avail = (size_t)fsz - off;
    /* lua_setfield is ~0x76 bytes / ~32 instructions; decode 48 to be safe. */
    size_t n = cs_disasm(h, buf + off, avail, rva, 48, &ins);
    if (n == 0) {
        printf("[disasm_check] FAIL: no instructions decoded at RVA 0x%x\n", rva);
        cs_close(&h); free(buf); return 1;
    }

    setfield_features_t feat; memset(&feat, 0, sizeof(feat));
    feat.all_calls_in_cluster = 1;

    for (size_t i = 0; i < n; i++) {
        classify_setfield_insn(&ins[i], i, ins, n, &feat);
        /* Stop at function boundary: RET or tail-call JMP (target outside
         * the function body — see pushcclosure matcher for the rationale). */
        if (ins[i].id == X86_INS_RET) break;
        if (ins[i].id == X86_INS_JMP) {
            unsigned long long tgt = strtoull(ins[i].op_str, NULL, 0);
            if (tgt < rva || tgt > rva + 0x100) {
                feat.body_end_rva = (uint32_t)(ins[i].address + ins[i].size);
                break;
            }
        }
    }
    uint32_t body_size = feat.body_end_rva - rva;

    printf("[disasm_check] decoded %zu insns at RVA 0x%x (file off 0x%x)\n",
           n, rva, off);
    printf("[disasm_check] setfield features:"
           " save_rsi_from_rcx=%d save_rbx_from_r8=%d str_tag(0xFB)=%d"
           " top_decrement=%d direct_calls=%d all_in_cluster=%d body_size=0x%x\n",
           feat.has_save_rsi_from_rcx, feat.has_save_rbx_from_r8, feat.has_str_tag,
           feat.has_top_decrement, feat.n_direct_calls, feat.all_calls_in_cluster,
           body_size);

    int ok = feat.has_save_rsi_from_rcx &&
             feat.has_save_rbx_from_r8 &&
             feat.has_str_tag &&
             feat.has_top_decrement &&
             feat.n_direct_calls == 4 &&
             feat.all_calls_in_cluster &&
             body_size <= 0x100;

    if (!ok) {
        printf("[disasm_check] FAIL: shape does not match lua_setfield\n");
        printf("[disasm_check] --- disassembly (first 40 insns) ---\n");
        size_t lim = n < 40 ? n : 40;
        for (size_t i = 0; i < lim; i++)
            printf("[disasm_check]   0x%06llx: %-8s %s\n",
                   (unsigned long long)ins[i].address,
                   ins[i].mnemonic, ins[i].op_str);
        printf("[disasm_check] --- end disassembly ---\n");
    } else {
        printf("[disasm_check] OK: shape matches lua_setfield @ RVA 0x%x\n", rva);
    }

    cs_free(ins, n);
    cs_close(&h);
    free(buf);
    return ok ? 0 : 1;
}
