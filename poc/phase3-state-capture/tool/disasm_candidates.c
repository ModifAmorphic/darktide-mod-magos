/*
 * tool/disasm_candidates.c — Phase 3: disassemble the 3 viable lua_pcall
 * candidates (and the pruned 4th) to extract arg-count evidence.
 *
 * Loads Darktide.exe via LoadLibraryExW(LOAD_LIBRARY_AS_IMAGE_RESOURCE |
 * LOAD_LIBRARY_AS_DATAFILE), reads the bytes at each candidate RVA, runs
 * capstone over the first ~20 instructions, prints the disassembly plus
 * a short arg-count analysis.
 *
 * Arg-count evidence model (Windows x64 calling convention):
 *   - arg 0 (L)            : rcx
 *   - arg 1                : rdx
 *   - arg 2                : r8
 *   - arg 3                : r9
 *   - args 4+              : stack [rsp+0x28], [rsp+0x30], ...
 *
 * Heuristic for "does the function use arg N?":
 *   - If the function READS rcx/rdx/r8/r9 before writing it (via mov reg,
 *     [reg+x]; sub/add reg, x; or any operand where the register is read),
 *     that register carries an input.
 *   - If the function first stores the register into the home area
 *     ([rsp+8/0x10/0x18/0x20]) without reading it, that's still an input
 *     (the engine uses the home-area copy later).
 *   - For lua_pcall(L, int nargs, int nresults, int errfunc): rcx=L,
 *     rdx/r8/r9 = the 3 ints. lua_pcall body reads all 4.
 *
 * For the pruned 0xc744c0: confirmed to call lua_load (per the Phase 2b
 * report). Included for comparison; not hookable.
 *
 * Run:
 *   build/disasm_candidates.exe [path-to-Darktide.exe]
 *
 * Default path is the live install.
 */
#include <capstone/capstone.h>
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define OFF_FROM_LFANEW_SIZE_OF_IMAGE  0x50u
#define NUM_INSNS 24

typedef struct {
    uint32_t rva;
    const char *label;
    const char *note;
} candidate_t;

static const candidate_t kCands[] = {
    { 0xc748d0u, "0xc748d0", "viable (cluster top score 90, callee=0xc82fc0)" },
    { 0xc74f30u, "0xc74f30", "viable (cluster top score 90, callee=0xc7ed10)" },
    { 0xc754d0u, "0xc754d0", "viable (cluster top score 90, callee=0xc7ed10)" },
    { 0xc744c0u, "0xc744c0", "PRUNED (calls lua_load — a luaL_load* wrapper)" },
};
static const int kCandsCount = (int)(sizeof(kCands) / sizeof(kCands[0]));

/* Track register reads in the first NUM_INSNS instructions of each
 * candidate. Arg N is "used" if the corresponding register is read. */
typedef struct {
    int reads_rcx, reads_rdx, reads_r8, reads_r9;
    int reads_rsp_off28, reads_rsp_off30, reads_rsp_off38, reads_rsp_off40;
    int stores_rcx_home, stores_rdx_home, stores_r8_home, stores_r9_home;
    /* Spill-restore detection: the prologue writes callee-saved regs to
     * [entry_rsp + X] (where X is in the shadow space, e.g. +8/0x10/0x18/
     * 0x20). After push/sub, the body reads them back at [body_rsp +
     * (X + frame_size)]. We track those restore offsets so we can ignore
     * them when scanning for true stack-arg reads. */
    int frame_size;             /* bytes pushed + subtracted in prologue */
    int spill_offsets_count;
    int64_t spill_offsets[8];   /* entry-relative offsets written in prologue */
} arg_use_t;

static int reg_name_eq(const char *op, const char *name) {
    return op && strcmp(op, name) == 0;
}

/* Returns 1 if [body_rsp + Y] is a spill-restore slot (i.e. the prologue
 * wrote the corresponding entry-relative offset and the body is reading
 * the spilled value back). */
static int is_spill_restore(const arg_use_t *u, int64_t body_off) {
    int64_t entry_off = body_off - u->frame_size;
    for (int i = 0; i < u->spill_offsets_count; ++i)
        if (u->spill_offsets[i] == entry_off) return 1;
    return 0;
}

/* Record register reads in a single instruction. We look at all source
 * operands (regs used as inputs). Capstone populates op.access with
 * CS_AC_READ on read operands. */
static void tally_instruction(csh csh, cs_insn *ins, arg_use_t *u) {
    /* For each register operand marked as read, mark the corresponding
     * arg-use flag. We map x64 subregisters (ecx, rcx, cl, cx) to rcx. */
    cs_detail *d = ins->detail;
    if (!d) return;
    for (uint8_t i = 0; i < d->x86.op_count; ++i) {
        cs_x86_op *op = &d->x86.operands[i];
        if (op->type != X86_OP_REG) continue;
        if (!(op->access & CS_AC_READ)) continue;
        const char *r = cs_reg_name(csh, op->reg);
        if (!r) continue;
        if      (reg_name_eq(r, "rcx") || reg_name_eq(r, "ecx") ||
                 reg_name_eq(r, "cx")  || reg_name_eq(r, "cl")) u->reads_rcx = 1;
        else if (reg_name_eq(r, "rdx") || reg_name_eq(r, "edx") ||
                 reg_name_eq(r, "dx")  || reg_name_eq(r, "dl")) u->reads_rdx = 1;
        else if (reg_name_eq(r, "r8")  || reg_name_eq(r, "r8d")  ||
                 reg_name_eq(r, "r8w") || reg_name_eq(r, "r8b")) u->reads_r8 = 1;
        else if (reg_name_eq(r, "r9")  || reg_name_eq(r, "r9d")  ||
                 reg_name_eq(r, "r9w") || reg_name_eq(r, "r9b")) u->reads_r9 = 1;
    }
    /* Detect home-area stores: mov [rsp+0x8], rcx ; mov [rsp+0x10], rdx ;
     * mov [rsp+0x18], r8 ; mov [rsp+0x20], r9 . This is how the engine
     * spills args to the home area in debug builds. */
    for (uint8_t i = 0; i < d->x86.op_count; ++i) {
        cs_x86_op *dst = &d->x86.operands[i];
        if (dst->type != X86_OP_MEM) continue;
        if (dst->mem.base != X86_REG_RSP) continue;
        int64_t disp = dst->mem.disp;
        /* Need the source register on this same instruction. */
        if (d->x86.op_count < 2) continue;
        cs_x86_op *src = NULL;
        for (uint8_t j = 0; j < d->x86.op_count; ++j) {
            if (j == i) continue;
            if (d->x86.operands[j].type == X86_OP_REG &&
                (d->x86.operands[j].access & CS_AC_READ)) {
                src = &d->x86.operands[j];
                break;
            }
        }
        if (!src) continue;
        const char *sr = cs_reg_name(csh, src->reg);
        if (!sr) continue;
        if      (disp == 0x08 && (reg_name_eq(sr, "rcx") || reg_name_eq(sr, "ecx"))) u->stores_rcx_home = 1;
        else if (disp == 0x10 && (reg_name_eq(sr, "rdx") || reg_name_eq(sr, "edx"))) u->stores_rdx_home = 1;
        else if (disp == 0x18 && (reg_name_eq(sr, "r8")  || reg_name_eq(sr, "r8d"))) u->stores_r8_home = 1;
        else if (disp == 0x20 && (reg_name_eq(sr, "r9")  || reg_name_eq(sr, "r9d"))) u->stores_r9_home = 1;
    }
    /* Detect reads of stack args beyond the 4th: [rsp+0x28], [rsp+0x30], ...
     * (would indicate a 5+ arg signature — unsafe for 4-arg detour).
     * IMPORTANT: skip reads whose body-relative offset matches a prologue
     * spill-restore slot. Those reads are callee-saved register restores,
     * NOT stack args. Failing to do this gives false "5+ arg" positives
     * on functions like 0xc748d0 whose epilogue restores rbx/rsi from
     * spill slots at [body rsp + 0x30] / [body rsp + 0x38]. */
    for (uint8_t i = 0; i < d->x86.op_count; ++i) {
        cs_x86_op *op = &d->x86.operands[i];
        if (op->type != X86_OP_MEM) continue;
        if (op->mem.base != X86_REG_RSP) continue;
        if (!(op->access & CS_AC_READ)) continue;
        int64_t disp = op->mem.disp;
        if (disp < 0x28) continue;             /* within shadow space */
        if (is_spill_restore(u, disp)) continue;
        if      (disp == 0x28) u->reads_rsp_off28 = 1;
        else if (disp == 0x30) u->reads_rsp_off30 = 1;
        else if (disp == 0x38) u->reads_rsp_off38 = 1;
        else if (disp == 0x40) u->reads_rsp_off40 = 1;
        else if (disp > 0x28) {
            /* Larger offsets are also potential stack args. Lump them
             * into the 0x28+ bucket so the safety flag fires. */
            u->reads_rsp_off28 = 1;
        }
    }
}

/* Scan a single PROLOGUE instruction (one of: mov [rsp+X], reg; push reg;
 * sub rsp, X). Updates frame_size and spill_offsets. Returns 1 if the
 * instruction looked like a prologue instruction, 0 if not (prologue end).
 *
 * x64 prologues are flexible but for the candidates we care about, the
 * prologue is the simple `mov [rsp+X], reg / push reg / sub rsp, X` run. */
static int tally_prologue(cs_insn *ins, arg_use_t *u) {
    cs_detail *d = ins->detail;
    if (!d) return 0;
    const char *mn = ins->mnemonic;
    /* push reg */
    if (strcmp(mn, "push") == 0 && d->x86.op_count == 1 &&
        d->x86.operands[0].type == X86_OP_REG) {
        u->frame_size += 8;
        return 1;
    }
    /* sub rsp, imm */
    if (strcmp(mn, "sub") == 0 && d->x86.op_count == 2 &&
        d->x86.operands[0].type == X86_OP_REG &&
        d->x86.operands[0].reg == X86_REG_RSP &&
        d->x86.operands[1].type == X86_OP_IMM) {
        u->frame_size += (int)d->x86.operands[1].imm;
        return 1;
    }
    /* mov [rsp+X], reg — spill of a callee-saved register */
    if (strcmp(mn, "mov") == 0 && d->x86.op_count == 2 &&
        d->x86.operands[0].type == X86_OP_MEM &&
        d->x86.operands[0].mem.base == X86_REG_RSP &&
        d->x86.operands[0].mem.index == X86_REG_INVALID &&
        d->x86.operands[1].type == X86_OP_REG) {
        int64_t off = d->x86.operands[0].mem.disp;
        if (u->spill_offsets_count < 8 && off >= 0 && off < 0x28) {
            u->spill_offsets[u->spill_offsets_count++] = off;
        }
        return 1;
    }
    return 0;
}

static void print_use_summary(const arg_use_t *u) {
    printf("    prologue: frame_size=0x%x, %d callee-saved spill(s)",
           u->frame_size, u->spill_offsets_count);
    if (u->spill_offsets_count) {
        printf(" at entry-relative");
        for (int i = 0; i < u->spill_offsets_count; ++i)
            printf(" [rsp+0x%llx]", (unsigned long long)u->spill_offsets[i]);
    }
    printf("\n");
    printf("    register reads: rcx=%d rdx=%d r8=%d r9=%d\n",
           u->reads_rcx, u->reads_rdx, u->reads_r8, u->reads_r9);
    printf("    home-area stores: rcx=%d rdx=%d r8=%d r9=%d\n",
           u->stores_rcx_home, u->stores_rdx_home,
           u->stores_r8_home, u->stores_r9_home);
    printf("    stack-arg reads (5+ args, would be UNSAFE for 4-arg detour): "
           "[rsp+0x28]=%d [rsp+0x30]=%d [rsp+0x38]=%d [rsp+0x40]=%d\n",
           u->reads_rsp_off28, u->reads_rsp_off30,
           u->reads_rsp_off38, u->reads_rsp_off40);
    int arg_count = 0;
    if (u->reads_rcx || u->stores_rcx_home) arg_count = 1;
    if (u->reads_rdx || u->stores_rdx_home) arg_count = 2;
    if (u->reads_r8  || u->stores_r8_home)  arg_count = 3;
    if (u->reads_r9  || u->stores_r9_home)  arg_count = 4;
    int unsafe = u->reads_rsp_off28 || u->reads_rsp_off30 ||
                 u->reads_rsp_off38 || u->reads_rsp_off40;
    if (unsafe) arg_count = 5;  /* stack args present -> at least 5 args */
    printf("    >>> estimated arg count: %d\n", arg_count);
    printf("    >>> 4-arg detour safety: %s\n",
           unsafe ? "UNSAFE (reads stack args beyond r9 — possible 5+ arg fn)" :
                    "SAFE (no stack-arg reads observed after filtering spill-restores)");
}

static int looks_like_mapped_pe(uintptr_t base) {
    if (base == 0) return 0;
    const uint8_t *p = (const uint8_t *)base;
    if (p[0] != 'M' || p[1] != 'Z') return 0;
    uint32_t e_lfanew = *(const uint32_t *)(p + 0x3C);
    if (e_lfanew == 0 || e_lfanew > (1u << 20)) return 0;
    if (memcmp(p + e_lfanew, "PE\0\0", 4) != 0) return 0;
    return 1;
}

int main(int argc, char **argv) {
    const char *utf8_path = (argc > 1) ? argv[1] :
        "Z:\\games\\steamapps\\common\\Warhammer 40,000 DARKTIDE\\binaries\\Darktide.exe";

    WCHAR wpath[MAX_PATH];
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8_path, -1, wpath, MAX_PATH);
    if (n == 0) {
        printf("[disasm] FAIL: path conversion failed for %s\n", utf8_path);
        return 2;
    }
    printf("[disasm] loading %s as mapped image\n", utf8_path);

    /* Try the IMAGE flag first; fallback to plain DATAFILE. */
    DWORD flags[] = {
        LOAD_LIBRARY_AS_IMAGE_RESOURCE | LOAD_LIBRARY_AS_DATAFILE,
        LOAD_LIBRARY_AS_IMAGE_RESOURCE,
        LOAD_LIBRARY_AS_DATAFILE,
    };
    HMODULE h = NULL;
    for (int i = 0; i < 3 && !h; ++i) h = LoadLibraryExW(wpath, NULL, flags[i]);
    if (!h) {
        printf("[disasm] FAIL: LoadLibraryEx failed err=%lu\n", GetLastError());
        return 3;
    }
    uintptr_t base = (uintptr_t)h & ~((uintptr_t)0x3);
    if (!looks_like_mapped_pe(base)) {
        printf("[disasm] FAIL: mapped base doesn't look like a PE image\n");
        return 4;
    }
    const uint8_t *p = (const uint8_t *)base;
    uint32_t e_lfanew = *(const uint32_t *)(p + 0x3C);
    uint32_t size_of_image = *(const uint32_t *)(p + e_lfanew + OFF_FROM_LFANEW_SIZE_OF_IMAGE);
    printf("[disasm] base=0x%llx size_of_image=0x%x\n",
           (unsigned long long)base, size_of_image);

    csh csh;
    if (cs_open(CS_ARCH_X86, CS_MODE_64, &csh) != CS_ERR_OK) {
        printf("[disasm] FAIL: cs_open failed\n");
        return 5;
    }
    cs_option(csh, CS_OPT_DETAIL, CS_OPT_ON);

    for (int i = 0; i < kCandsCount; ++i) {
        const candidate_t *c = &kCands[i];
        printf("\n[disasm] ===== candidate %s  %s =====\n", c->label, c->note);
        if (c->rva >= size_of_image) {
            printf("    SKIP: rva=0x%x beyond SizeOfImage\n", c->rva);
            continue;
        }
        const uint8_t *code = p + c->rva;
        size_t code_size = size_of_image - c->rva;
        cs_insn *insns = cs_malloc(csh);
        uint64_t addr = c->rva;
        size_t remaining = code_size;
        int count = 0;
        arg_use_t u; memset(&u, 0, sizeof(u));
        printf("    first %d instructions (file-offset view; rva-relative):\n", NUM_INSNS);
        int prologue_done = 0;
        int epilogue_started = 0;
        while (count < NUM_INSNS &&
               cs_disasm_iter(csh, &code, &remaining, &addr, insns)) {
            char hex[64] = "";
            int hn = 0;
            for (int k = 0; k < (int)insns->size && k < 15; ++k)
                hn += snprintf(hex + hn, sizeof(hex) - hn, "%02x ", insns->bytes[k]);
            printf("    %016llx  %-44s %s %s\n",
                   (unsigned long long)insns->address,
                   hex, insns->mnemonic, insns->op_str);

            /* Phase 1: scan prologue instructions (push/sub/mov [rsp+X],reg)
             * to learn the frame size and spill-slot offsets. The prologue
             * ends at the first instruction that isn't one of these. */
            if (!prologue_done) {
                if (tally_prologue(insns, &u)) {
                    ++count;
                    continue;
                }
                prologue_done = 1;
            }

            /* Phase 2: scan body. Stop tallying once the epilogue begins:
             * once we see `add rsp, X`, `pop <reg>`, `ret`, or a tail
             * `jmp`. After that, [rsp+X]+ reads are callee-saved register
             * restores (matching the prologue's spills), NOT stack-arg
             * reads. Failing to do this gives false "5+ arg" positives. */
            if (!epilogue_started) {
                if (strcmp(insns->mnemonic, "add")  == 0 ||
                    strcmp(insns->mnemonic, "pop")  == 0 ||
                    strcmp(insns->mnemonic, "ret")  == 0 ||
                    strcmp(insns->mnemonic, "jmp")  == 0) {
                    epilogue_started = 1;
                } else {
                    tally_instruction(csh, insns, &u);
                }
            }
            ++count;
        }
        cs_free(insns, 1);
        if (count == 0) {
            printf("    (capstone disassembled 0 instructions; rva may not be code)\n");
            continue;
        }
        printf("\n");
        print_use_summary(&u);
    }

    cs_close(&csh);
    FreeLibrary(h);
    printf("\n[disasm] done\n");
    return 0;
}
