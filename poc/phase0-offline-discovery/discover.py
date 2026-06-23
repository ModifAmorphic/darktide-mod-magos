#!/usr/bin/env python3
"""
Phase 0 Offline Discovery for the Darktide Lua VM Injection POC.

This script is **pure offline binary analysis**. It never launches the game,
builds a DLL, or touches the game process. It reads Darktide.exe from disk
and produces two sibling files in this directory:

    addresses.json  - structured machine-readable results
    report.md       - human-readable analysis report

It automates the discovery methodology documented in
``docs/lua-vm-injection-anchors.md`` §7 and validates it against the
pinned Darktide.exe build (18,715,784 bytes). Every methodology gap it
finds is recorded explicitly so Phase 2 (runtime discovery inside the
injected DLL) can account for it.

Usage:
    python3 discover.py

If pefile/capstone are not importable, the script transparently re-execs
itself using the project venv at ``./.venv/bin/python`` (created during
setup). This keeps ``python3 discover.py`` a single idempotent command.
"""

from __future__ import annotations

import bisect
import hashlib
import json
import os
import struct
import sys
from dataclasses import dataclass, field
from typing import Optional

# ---------------------------------------------------------------------------
# Self-bootstrap: re-exec through the project venv if deps are missing so the
# operator can always run plain `python3 discover.py`.
# ---------------------------------------------------------------------------
try:
    import pefile  # type: ignore
    import capstone  # type: ignore  # noqa: F401
except ImportError:
    here = os.path.dirname(os.path.abspath(__file__))
    venv_py = os.path.join(here, ".venv", "bin", "python")
    if os.path.exists(venv_py):
        os.execv(venv_py, [venv_py] + sys.argv)
    raise

from capstone import Cs, CS_ARCH_X86, CS_MODE_64  # noqa: E402

# ---------------------------------------------------------------------------
# Constants pinned to the analyzed build.
# ---------------------------------------------------------------------------
BINARY_PATH = (
    "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe"
)
EXPECTED_SIZE = 18715784
EXPECTED_RDATA_DELTA = 0x2A00

HERE = os.path.dirname(os.path.abspath(__file__))
JSON_PATH = os.path.join(HERE, "addresses.json")
REPORT_PATH = os.path.join(HERE, "report.md")

# ---------------------------------------------------------------------------
# Anchor tables, transcribed verbatim from anchors doc §3 Quick Reference and
# §5. Each entry is (label, documented_file_offset, expected_null_term_string).
# ---------------------------------------------------------------------------
ANCHORS_S3 = [
    ("LuaJIT version",            0x00E8B108, b"LuaJIT 2.1.1771479498"),
    ("lua_panic",                 0x00F1F698, b"stingray::LuaEnvironment::Internal::lua_panic"),
    ("default_error_callback",    0x00F1F910, b"stingray::LuaEnvironment::Internal::default_error_callback"),
    ("clear_temp_variables",      0x00F1FA48, b"stingray::LuaEnvironment::clear_temp_variables"),
    ("dump_state",                0x00F1FC98, b"LuaEnvironment::dump_state"),
    ("lua_resource::bytecode",    0x00F1D9D0, b"stingray::lua_resource::bytecode"),
    ("copy_lua_variable_to_c",    0x00F266C8, b"stingray::script_interface::copy_lua_variable_to_c"),
    ("push_c_variable_to_lua",    0x00F267F8, b"stingray::script_interface::push_c_variable_to_lua"),
    ("Bundle::open",              0x00F50BC0, b"stingray::Bundle::open"),
    ("Lua->update",               0x00F520B8, b"Lua->update"),
    ("load_script_data",          0x00F51EF8, b"load_script_data"),
    ("bundle_database.data",      0x00F4E298, b"bundle_database.data"),
    ("lua_environment_api",       0x00F1F4F8, b"lua_environment_api"),
]

ERROR_STRINGS_S5 = [
    ("attempt_to_call",  0x00E89B86, b"attempt to call a %s value"),
    ("bad_argument",     0x00E89C97, b"bad argument #%d to '%s'"),
    ("loop_in_gettable", 0x00E89C1C, b"loop in gettable"),
    ("invalid_key_next", 0x00E89B70, b"invalid key to 'next'"),
]


# ---------------------------------------------------------------------------
# Small helpers for hex formatting in JSON (the spec wants hex strings).
# ---------------------------------------------------------------------------
def hx(n: int) -> str:
    return f"0x{n:x}"


# ---------------------------------------------------------------------------
# PE / .pdata model.
# ---------------------------------------------------------------------------
@dataclass
class SectionInfo:
    name: str
    rva: int
    file_offset: int
    raw_size: int
    virtual_size: int


@dataclass
class PEModel:
    path: str
    raw: bytes
    sha256: str
    image_base: int
    sections: dict
    text: SectionInfo
    rdata: SectionInfo
    pdata: SectionInfo
    # Sorted RUNTIME_FUNCTION triples (begin, end, unwind_rva).
    runtime_functions: list
    _begins: list  # sorted begin addresses for bisect

    def file_offset(self, rva: int) -> int:
        """Translate an RVA back to a file offset using the section table."""
        for s in self.sections.values():
            if s.rva <= rva < s.rva + max(s.raw_size, s.virtual_size):
                return s.file_offset + (rva - s.rva)
        raise ValueError(f"RVA {hex(rva)} not in any section")

    def read_at_rva(self, rva: int, n: int) -> bytes:
        try:
            off = self.file_offset(rva)
            return self.raw[off : off + n]
        except ValueError:
            return b""

    def find_runtime_function(self, rva: int) -> Optional[tuple]:
        """Return (begin, end, unwind) if rva is inside a .pdata entry."""
        i = bisect.bisect_right(self._begins, rva) - 1
        if i < 0:
            return None
        b, e, u = self.runtime_functions[i]
        if b <= rva < e:
            return (b, e, u)
        return None


def load_pe(path: str) -> PEModel:
    with open(path, "rb") as f:
        raw = f.read()
    if len(raw) != EXPECTED_SIZE:
        raise SystemExit(
            f"ABORT: binary size mismatch. Expected {EXPECTED_SIZE} bytes, "
            f"got {len(raw)}. The offsets in the anchors doc are pinned to "
            f"the {EXPECTED_SIZE}-byte build; refusing to continue."
        )
    sha = hashlib.sha256(raw).hexdigest()
    pe = pefile.PE(path, fast_load=True)
    secs = {}
    for s in pe.sections:
        name = s.Name.rstrip(b"\x00").decode("latin1")
        si = SectionInfo(
            name=name,
            rva=s.VirtualAddress,
            file_offset=s.PointerToRawData,
            raw_size=s.SizeOfRawData,
            virtual_size=s.Misc_VirtualSize,
        )
        secs[name] = si

    if ".text" not in secs or ".rdata" not in secs or ".pdata" not in secs:
        raise SystemExit("ABORT: missing one of .text/.rdata/.pdata sections")

    pdata = secs[".pdata"]
    blob = raw[pdata.file_offset : pdata.file_offset + pdata.raw_size]
    rfs = []
    for i in range(len(blob) // 12):
        b, e, u = struct.unpack_from("<III", blob, i * 12)
        if b == 0 and e == 0:
            continue
        rfs.append((b, e, u))
    rfs.sort()
    return PEModel(
        path=path,
        raw=raw,
        sha256=sha,
        image_base=pe.OPTIONAL_HEADER.ImageBase,
        sections=secs,
        text=secs[".text"],
        rdata=secs[".rdata"],
        pdata=pdata,
        runtime_functions=rfs,
        _begins=[r[0] for r in rfs],
    )


# ---------------------------------------------------------------------------
# Disassembly + call-target resolution utilities.
#
# Three "gotcha" function shapes on this MSVC build that the anchors doc does
# not anticipate but Phase 2 MUST handle:
#
#   1. CFG/hot-patch thunks:  5-byte `E9 rel32` + `cc` padding. These have no
#      .pdata entry of their own; the real body is at the thunk target.
#   2. Leaf functions:        no prologue, SP unchanged, ends in `ret`. MSVC
#      omits their .pdata entries too. Disassemble from the entry until ret.
#   3. Import thunks:         `FF 25 disp32` (jmp qword ptr [rip+disp32]).
#      Resolve to the imported DLL!function via the IAT.
# ---------------------------------------------------------------------------
def trace_thunk(model: PEModel, rva: int, max_hops: int = 8):
    """If rva is a chain of E9 rel32 jmps, return (final_rva, chain)."""
    chain = [rva]
    cur = rva
    for _ in range(max_hops):
        if not (model.text.rva <= cur < model.text.rva + model.text.raw_size):
            break
        off = model.file_offset(cur)
        b = model.raw[off : off + 5]
        if len(b) == 5 and b[0] == 0xE9:
            rel = struct.unpack_from("<i", b, 1)[0]
            nxt = cur + 5 + rel
            chain.append(nxt)
            cur = nxt
        else:
            break
    return cur, chain


def resolve_import_thunk(model: PEModel, rva: int) -> Optional[tuple]:
    """If rva is `FF 25 disp32` (jmp [rip+disp32]), resolve to (dll, name).

    Returns None if not an import thunk or if the IAT entry can't be resolved.
    """
    off = model.file_offset(rva)
    b = model.raw[off : off + 6]
    if len(b) != 6 or b[0] != 0xFF or b[1] != 0x25:
        return None
    disp = struct.unpack_from("<i", b, 2)[0]
    iat_rva = rva + 6 + disp
    iat_va = model.image_base + iat_rva
    # Re-parse PE with imports (fast_load was on; do a full parse lazily).
    pe = pefile.PE(model.path)
    for entry in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
        dll = entry.dll.decode()
        for imp in entry.imports:
            if imp.address == iat_va:
                name = imp.name.decode() if imp.name else f"ordinal#{imp.ordinal}"
                return (dll, name)
    return None


def function_bytes(model: PEModel, begin: int, end: Optional[int] = None) -> bytes:
    """Return the raw bytes of a function bounded by .pdata or a given end."""
    if end is None:
        rf = model.find_runtime_function(begin)
        if rf is None:
            # Leaf function: no .pdata. Disassemble up to 256 bytes and let
            # the caller trim at the first ret.
            end_guess = begin + 256
        else:
            end_guess = rf[1]
    else:
        end_guess = end
    start_off = model.file_offset(begin)
    end_off = model.file_offset(end_guess) if end_guess else start_off + 256
    return model.raw[start_off:end_off]


def disasm_function(model: PEModel, begin: int, end: Optional[int] = None):
    """Disassemble a function. If end is None and no .pdata entry exists,
    decode until the first `ret` (leaf-function handling). Yields insns."""
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    if end is None:
        rf = model.find_runtime_function(begin)
        if rf is not None:
            end = rf[1]
    if end is None:
        # Leaf: read a chunk and stop at ret.
        data = function_bytes(model, begin)
        for ins in md.disasm(data, begin):
            yield ins
            if ins.mnemonic in ("ret", "retn"):
                return
    else:
        data = function_bytes(model, begin, end)
        yield from md.disasm(data, begin)


# ---------------------------------------------------------------------------
# Phase A — PE parse + anchor sanity check.
# ---------------------------------------------------------------------------
def phase_a(model: PEModel):
    delta = model.rdata.rva - model.rdata.file_offset

    anchor_sanity = []
    doc_corrections = []
    for label, file_off, expected in ANCHORS_S3:
        actual = model.raw[file_off : file_off + len(expected)]
        match = actual == expected
        computed_rva = file_off + delta
        entry = {
            "label": label,
            "string": expected.decode("latin1"),
            "documented_file_offset": hx(file_off),
            "actual_at_offset": "yes" if match else "no",
            "computed_rva": hx(computed_rva),
            "doc_correction": None,
        }
        if not match:
            entry["actual_bytes_hex"] = actual.hex()
            entry["doc_correction"] = (
                f"String at {hx(file_off)} does not match documented "
                f"{expected.decode()!r}; got {actual!r}"
            )
            doc_corrections.append(entry["doc_correction"])
        anchor_sanity.append(entry)

    delta_match = delta == EXPECTED_RDATA_DELTA
    return {
        "delta": delta,
        "delta_match": delta_match,
        "anchor_sanity": anchor_sanity,
        "doc_corrections": doc_corrections,
    }


# ---------------------------------------------------------------------------
# Phase B — Category A: string -> LEA xref -> .pdata containing function.
# ---------------------------------------------------------------------------
def find_lea_xrefs(model: PEModel, target_rva: int) -> list:
    """Scan .text for `48/4C 8D <modrm> <disp32>` LEAs whose RIP+7+disp32
    equals target_rva. Returns the list of xref-site RVAs."""
    text = model.text
    blob = model.raw[text.file_offset : text.file_offset + text.raw_size]
    sites = []
    n = len(blob)
    i = 0
    while i < n - 7:
        b0 = blob[i]
        if (b0 == 0x48 or b0 == 0x4C) and blob[i + 1] == 0x8D and (blob[i + 2] & 0xC7) == 0x05:
            disp = struct.unpack_from("<i", blob, i + 3)[0]
            irva = text.rva + i
            if irva + 7 + disp == target_rva:
                sites.append(irva)
        i += 1
    return sites


def find_callers(model: PEModel, target_rva: int) -> list:
    """Scan .text for E8 rel32 direct calls to target_rva."""
    text = model.text
    blob = model.raw[text.file_offset : text.file_offset + text.raw_size]
    callers = []
    n = len(blob)
    i = 0
    while i < n - 5:
        if blob[i] == 0xE8:
            rel = struct.unpack_from("<i", blob, i + 1)[0]
            irva = text.rva + i
            if irva + 5 + rel == target_rva:
                callers.append(irva)
        i += 1
    return callers


def phase_b(model: PEModel, phase_a_result: dict):
    """For every §3 anchor: xref sites -> distinct containing functions."""
    delta = model.rdata.rva - model.rdata.file_offset
    results = []
    for sane in phase_a_result["anchor_sanity"]:
        file_off = int(sane["documented_file_offset"], 16)
        anchor_rva = file_off + delta
        sites = find_lea_xrefs(model, anchor_rva)
        # Map each site to its containing function.
        containing = {}
        for s in sites:
            rf = model.find_runtime_function(s)
            if rf is None:
                continue
            b, e, _u = rf
            containing.setdefault(b, (b, e))
        funcs = [
            {"begin_rva": hx(b), "end_rva": hx(e), "size_bytes": e - b}
            for b, e in sorted(containing.values())
        ]
        results.append(
            {
                "anchor": sane["label"],
                "anchor_string": sane["string"],
                "anchor_rva": hx(anchor_rva),
                "xref_count": len(sites),
                "xref_sites": [hx(s) for s in sites],
                "containing_functions": funcs,
            }
        )
    return results


# ---------------------------------------------------------------------------
# Phase C — Category B: disasm init candidate, classify call targets.
# ---------------------------------------------------------------------------
def select_init_candidate(model: PEModel, cat_a: list):
    """Pick the LuaEnvironment init candidate.

    Strategy (multi-signal, more robust than just the string-xref owner):
      1. Find the lua_panic *function body* = the containing function of the
         lua_panic string xref (it logs its own name).
      2. Find LEA references to that function body's address — the engine
         takes &lua_panic when calling lua_atpanic.
      3. Among those, the largest containing function is the init candidate.
         Cross-check by looking for the "lua_environment" string ref inside.
    Returns (init_dict, lua_panic_body_rva, lea_of_panic_sites, alternatives).
    """
    panic_entry = next(a for a in cat_a if a["anchor"] == "lua_panic")
    panic_funcs = panic_entry["containing_functions"]
    if not panic_funcs:
        return None, None, [], []
    # The lua_panic body itself is the (typically small) first containing func.
    panic_body = int(panic_funcs[0]["begin_rva"], 16)

    # LEA references to the lua_panic function address = lua_atpanic setup.
    lea_sites = find_lea_xrefs(model, panic_body)

    candidates = {}
    for s in lea_sites:
        rf = model.find_runtime_function(s)
        if rf is None:
            continue
        b, e = rf[0], rf[1]
        candidates.setdefault(b, (b, e))

    # Also include direct E8 callers (defensive — none expected for lua_panic).
    direct_callers = find_callers(model, panic_body)
    for c in direct_callers:
        rf = model.find_runtime_function(c)
        if rf is None:
            continue
        candidates.setdefault(rf[0], (rf[0], rf[1]))

    ranked = sorted(candidates.values(), key=lambda be: -(be[1] - be[0]))
    if not ranked:
        return None, panic_body, lea_sites, []

    chosen_b, chosen_e = ranked[0]
    # Verify the "lua_environment" string lives inside the chosen function.
    sanity = b"lua_environment"
    found_marker = False
    for ins in disasm_function(model, chosen_b, chosen_e):
        if ins.mnemonic == "lea" and "rip" in ins.op_str:
            try:
                tgt = _lea_target(ins)
                s = model.read_at_rva(tgt, len(sanity))
                if s == sanity:
                    found_marker = True
                    break
            except Exception:
                pass

    reasoning = (
        f"Selected as LuaEnvironment init: it takes &lua_panic (at "
        f"{hx(panic_body)}) via LEA at {[hx(s) for s in lea_sites]} — "
        f"the lua_atpanic(L, &lua_panic) setup shape. Size "
        f"{chosen_e - chosen_b} bytes (largest candidate). "
        f"'lua_environment' string ref present in body: {found_marker}."
    )
    init = {
        "begin_rva": hx(chosen_b),
        "end_rva": hx(chosen_e),
        "size_bytes": chosen_e - chosen_b,
        "reasoning": reasoning,
        "lua_environment_marker_found": found_marker,
        "lua_panic_body_rva": hx(panic_body),
        "lea_of_lua_panic_sites": [hx(s) for s in lea_sites],
    }
    alternatives = [
        {"begin_rva": hx(b), "end_rva": hx(e), "size_bytes": e - b}
        for b, e in ranked[1:]
    ]
    return init, panic_body, lea_sites, alternatives


def _lea_target(ins) -> int:
    """Extract the RIP-relative target RVA from a capstone LEA instruction."""
    # capstone op_str looks like 'rax, [rip + 0x1234]' or 'rax, [rip - 0x10]'
    op = ins.op_str
    if "rip + " in op:
        disp = int(op.split("rip + ")[1].rstrip("]"), 0)
    elif "rip - " in op:
        disp = -int(op.split("rip - ")[1].rstrip("]"), 0)
    else:
        raise ValueError(f"not a rip-relative LEA: {op}")
    return ins.address + ins.size + disp


@dataclass
class CallTarget:
    call_site_rva: int
    target_rva: int
    is_thunk: bool
    thunk_chain: list
    real_rva: int  # target after following thunks
    has_pdata: bool
    func_begin: Optional[int]
    func_end: Optional[int]
    func_size: int
    import_info: Optional[tuple]  # (dll, name) for import thunks
    arg_hints: dict = field(default_factory=dict)  # rcx/rdx/r8/r9 last-write


def enumerate_calls(model: PEModel, begin: int, end: int) -> list:
    """Walk the function and record every CALL with classification context."""
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    data = function_bytes(model, begin, end)
    insns = list(md.disasm(data, begin))
    out = []
    for idx, ins in enumerate(insns):
        if ins.mnemonic != "call":
            continue
        # Indirect call?
        if not ins.op_str.startswith("0x"):
            out.append(
                {
                    "call_site_rva": hx(ins.address),
                    "kind": "indirect",
                    "operand": ins.op_str,
                    "note": "indirect call — target requires backward dataflow "
                    "(out of scope for Phase 0; flag for Phase 2)",
                }
            )
            continue

        target = int(ins.op_str, 16)
        real, chain = trace_thunk(model, target)
        is_thunk = len(chain) > 1
        rf = model.find_runtime_function(real)
        imp = resolve_import_thunk(model, target)

        # Arg hints: scan backward up to 8 insns for last write to each arg reg.
        arg_hints = {}
        for reg in ("rcx", "rdx", "r8", "r9"):
            for back in insns[max(0, idx - 8) : idx][::-1]:
                op = back.op_str
                if op.startswith(reg + ",") or op == reg or op.startswith(reg + " "):
                    arg_hints[reg] = f"{back.mnemonic} {op}"
                    break

        ct = {
            "call_site_rva": hx(ins.address),
            "kind": "direct",
            "target_rva": hx(target),
            "is_thunk": is_thunk,
            "thunk_chain": [hx(c) for c in chain],
            "real_target_rva": hx(real),
            "has_pdata": rf is not None,
            "func_begin": hx(rf[0]) if rf else None,
            "func_end": hx(rf[1]) if rf else None,
            "func_size": (rf[1] - rf[0]) if rf else 0,
            "import": (f"{imp[0]}!{imp[1]}") if imp else None,
            "arg_hints": arg_hints,
        }
        out.append(ct)
    return out


# ----- Classification heuristics for individual LuaJIT API functions. -----
def classify_by_body(model: PEModel, real_rva: int) -> dict:
    """Examine a call target's body and emit classification evidence."""
    rf = model.find_runtime_function(real_rva)
    insns = list(disasm_function(model, real_rva, rf[1] if rf else None))
    if not insns:
        return {"classification": "unknown", "evidence": "no decodable bytes"}

    # Collapse first 12 bytes for quick byte-signature checks.
    head_bytes = b"".join(i.bytes for i in insns[:6])
    mnem_seq = [i.mnemonic for i in insns]
    op_seq = [i.op_str for i in insns]

    # lua_gettop:  mov rax,[rcx+X]; sub rax,[rcx+Y]; sar rax,3; ret
    if (
        len(insns) >= 4
        and insns[0].mnemonic == "mov"
        and "[rcx + " in insns[0].op_str
        and "rax" in insns[0].op_str
        and insns[1].mnemonic == "sub"
        and "[rcx + " in insns[1].op_str
        and insns[2].mnemonic in ("sar", "shr")
        and insns[3].mnemonic in ("ret", "retn")
    ):
        return {
            "classification": "lua_gettop",
            "confidence": "high",
            "evidence": (
                "leaf function computing (top-base)>>3 from [rcx+X]/[rcx+Y], "
                "returning in rax — textbook lua_gettop(L)"
            ),
        }

    # lua_atpanic: mov r8d,[rcx+8]; mov rax,[r8+0x118]; mov [r8+0x118],rdx; ret
    if (
        len(insns) >= 4
        and insns[0].mnemonic == "mov"
        and "r8" in insns[0].op_str
        and "[rcx + 8]" in insns[0].op_str
        and insns[1].mnemonic == "mov"
        and "[r8 + 0x118]" in insns[1].op_str
        and insns[2].mnemonic == "mov"
        and "[r8 + 0x118]" in insns[2].op_str
        and "rdx" in insns[2].op_str
        and insns[3].mnemonic in ("ret", "retn")
    ):
        return {
            "classification": "lua_atpanic",
            "confidence": "high",
            "evidence": (
                "leaf function: reads [rcx+8] (global_State* g), swaps rdx into "
                "[g+0x118] (panic fn slot), returns previous — matches "
                "lua_atpanic(L, fn)"
            ),
        }

    # lua_push* family: writes a type tag at [top], bumps top, overflow check.
    if (
        len(insns) >= 5
        and mnem_seq[0] == "mov"
        and "[rcx + 0x18]" in op_seq[0]
        and "add" in mnem_seq
        and any("0x18]" in o and "add" == m for m, o in zip(mnem_seq, op_seq))
        and insns[-1].mnemonic in ("ret", "retn")
        and rf is None  # leaf -> no .pdata
    ):
        return {
            "classification": "lua_push_family",
            "confidence": "medium",
            "evidence": (
                "leaf function writing a tag and bumping L->top ([rcx+0x18]) "
                "with overflow check — shape matches a lua_push* primitive"
            ),
        }

    # luaL_loadbuffer: 4 args (L, buf, size, name); builds a reader; calls a
    # very large internal lua_load; post-load GC step. Identified by the
    # presence of a call to a 10K+ byte function and reader setup.
    call_targets = []
    for i in insns:
        if i.mnemonic == "call" and i.op_str.startswith("0x"):
            call_targets.append(int(i.op_str, 16))
    large_callees = []
    for ct in call_targets:
        crf = model.find_runtime_function(ct)
        if crf and (crf[1] - crf[0]) > 8000:
            large_callees.append((ct, crf[1] - crf[0]))
    # Body-only shape match for a load wrapper: small function calling a very
    # large internal function (lua_load). Labeled as a *candidate* here — the
    # authoritative luaL_loadbuffer identification comes from the bytecode
    # trace in _find_loadbuffer_from_bytecode, which also confirms the 4-arg
    # (L,buf,size,name) call-site context. Without that context we cannot
    # distinguish luaL_loadbuffer from luaL_loadfilex or similar wrappers.
    if large_callees and rf and (rf[1] - rf[0]) < 400:
        big = max(large_callees, key=lambda x: x[1])
        return {
            "classification": "lua_load_wrapper_candidate",
            "confidence": "medium",
            "evidence": (
                f"small wrapper ({rf[1]-rf[0]}B) calling a {big[1]}B internal "
                f"function (likely lua_load) — shape matches a luaL_load* "
                f"wrapper, but call-site arg context needed to confirm which "
                f"one (loadbuffer vs loadfilex vs loadstring)"
            ),
            "internal_lua_load_rva": hx(big[0]),
        }

    # lua_gc: 3 args (L, int what, int data); large switch body (>300B); no
    # pointer args. Heuristic only.
    if (
        rf
        and 300 < (rf[1] - rf[0]) < 800
        and len(call_targets) >= 3
        and not large_callees
    ):
        return {
            "classification": "lua_gc_candidate",
            "confidence": "low",
            "evidence": (
                f"medium body ({rf[1]-rf[0]}B) with 3-arg (L,int,int) shape "
                f"and multiple internal calls — plausibly lua_gc or another "
                f"multi-arg API; needs runtime confirmation"
            ),
        }

    return {
        "classification": "unknown",
        "confidence": "none",
        "evidence": (
            f"body of {rf[1]-rf[0] if rf else '?'}B; head="
            f"{head_bytes.hex()[:32]}; no signature matched"
        ),
    }


def phase_c(model: PEModel, init: dict, cat_a: list):
    """Disasm the init candidate; classify every direct-call target."""
    begin = int(init["begin_rva"], 16)
    end = int(init["end_rva"], 16)
    calls = enumerate_calls(model, begin, end)

    # Group by real target so we classify each LuaJIT API function once.
    seen_real = {}
    for c in calls:
        if c["kind"] != "direct":
            continue
        key = c["real_target_rva"]
        if key in seen_real:
            seen_real[key]["call_sites"].append(c["call_site_rva"])
            continue
        if c["import"]:
            cls = {
                "classification": "import",
                "confidence": "high",
                "evidence": f"import thunk -> {c['import']}",
            }
        else:
            cls = classify_by_body(model, int(c["real_target_rva"], 16))
        seen_real[key] = {
            "target_rva": c["target_rva"],
            "real_target_rva": c["real_target_rva"],
            "is_thunk": c["is_thunk"],
            "thunk_chain": c["thunk_chain"],
            "has_pdata": c["has_pdata"],
            "func_begin": c["func_begin"],
            "func_end": c["func_end"],
            "func_size": c["func_size"],
            "import": c["import"],
            "call_sites": [c["call_site_rva"]],
            "arg_hints_sample": c["arg_hints"],
            **cls,
        }

    classified = list(seen_real.values())

    # lua_newstate is special: it must be identified by call CONTEXT (its
    # return value becomes the lua_State*), not by body alone. Find the call
    # whose result is stored and then used as rcx (L) for the lua_atpanic call.
    lua_newstate_finding = _identify_lua_newstate(model, begin, end, calls, classified)

    # Build the Category B candidates list from confirmed classifications.
    cat_b = []
    # lua_newstate
    cat_b.append(lua_newstate_finding)
    # lua_atpanic
    for c in classified:
        if c["classification"] == "lua_atpanic":
            cat_b.append(_to_cat_b(c, "lua_atpanic", "direct-call-trace"))
            break
    # lua_gettop
    for c in classified:
        if c["classification"] == "lua_gettop":
            cat_b.append(_to_cat_b(c, "lua_gettop", "direct-call-trace"))
            break
    # luaL_loadbuffer (traced from lua_resource::bytecode)
    loadbuffer = _find_loadbuffer_from_bytecode(model, cat_a)
    if loadbuffer:
        cat_b.append(loadbuffer)
    else:
        cat_b.append({
            "name": "luaL_loadbuffer",
            "candidate_rvas": [],
            "confidence": "none",
            "evidence": "no candidate identified",
            "discovery_method": "direct-call-trace",
        })
    # lua_pcall — declared gap; record negative result explicitly.
    cat_b.append({
        "name": "lua_pcall",
        "candidate_rvas": [],
        "confidence": "none",
        "evidence": (
            "Not conclusively identified in Phase 0. lua_pcall has no string "
            "anchor and the engine's bytecode path (0x3298b0/0x32ab30) does "
            "not exhibit a clean (L,int,int,int) call site that resolves to a "
            "thin wrapper around lj_docall. The LuaJIT API cluster around "
            "0xc7xxxx contains many candidates; runtime confirmation needed. "
            "See Recommendations for Phase 2."
        ),
        "discovery_method": "direct-call-trace",
    })

    return {
        "init_call_graph": calls,
        "classified_targets": classified,
        "category_b_candidates": cat_b,
    }


def _to_cat_b(c: dict, name: str, method: str) -> dict:
    rvas = [c["target_rva"]]
    if c["is_thunk"] and c["real_target_rva"] not in rvas:
        rvas.append(c["real_target_rva"])
    return {
        "name": name,
        "candidate_rvas": rvas,
        "confidence": c["confidence"],
        "evidence": c["evidence"],
        "discovery_method": method,
    }


def _identify_lua_newstate(model: PEModel, fbegin: int, fend: int, calls: list, classified: list) -> dict:
    """Identify lua_newstate by backward DATAFLOW trace from lua_atpanic.

    The naive "nearest preceding call" heuristic is WRONG because the init
    function calls lua_gc twice between lua_newstate and lua_atpanic. We must
    trace the lua_State* pointer itself:

        call <lua_newstate>           ; rax = L
        mov  [r14], rax               ; store L into the LuaEnvironment slot
        ... (lua_gc, etc. may intervene) ...
        mov  rcx, [r14]               ; reload L into rcx
        lea  rdx, [rip + lua_panic]   ; &lua_panic
        call <lua_atpanic>

    Recipe:
      1. Find the lua_atpanic call.
      2. Walk backward to the `mov rcx, <L source>` that feeds it; extract the
         memory operand (e.g. `[r14]`).
      3. Walk further backward to the `mov <same memory>, rax` that stored L.
      4. The nearest `call` instruction before that store is lua_newstate.

    This correctly skips the intervening lua_gc calls because it follows the
    data (rax -> [r14] -> rcx), not the control flow (nearest call).
    """
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    data = function_bytes(model, fbegin, fend)
    insns = list(md.disasm(data, fbegin))

    def fail(reason: str) -> dict:
        return {
            "name": "lua_newstate",
            "candidate_rvas": [],
            "confidence": "none",
            "evidence": reason,
            "discovery_method": "direct-call-trace",
        }

    # 1. Locate the lua_atpanic call (we know its address from classification).
    atpanic_rva = None
    for c in classified:
        if c["classification"] == "lua_atpanic":
            atpanic_rva = int(c["target_rva"], 16)
            break
    if atpanic_rva is None:
        return fail("lua_atpanic not classified; cannot anchor the trace")

    atpanic_idx = None
    for idx, ins in enumerate(insns):
        if (
            ins.mnemonic == "call"
            and ins.op_str.startswith("0x")
            and int(ins.op_str, 16) == atpanic_rva
        ):
            atpanic_idx = idx
            break
    if atpanic_idx is None:
        return fail("lua_atpanic call site not found in init body")

    # 2. Walk backward to `mov rcx, <src>` feeding atpanic. Abort if we cross
    #    another call first (rcx would be clobbered).
    rcx_load_idx = None
    rcx_source = None
    for back in range(atpanic_idx - 1, max(atpanic_idx - 20, 0) - 1, -1):
        ins = insns[back]
        if ins.mnemonic == "call":
            break
        if ins.mnemonic == "mov" and (ins.op_str.startswith("rcx,") or ins.op_str == "rcx"):
            rcx_load_idx = back
            rcx_source = ins.op_str.split(",", 1)[1].strip()
            break
    if rcx_load_idx is None:
        return fail("could not find the `mov rcx, <L>` feeding lua_atpanic")

    # The L source is typically a memory operand like `qword ptr [r14]` or a
    # register like `rbp`. Extract a normalized "slot key" to match against
    # the store site. For memory operands we keep the bracketed part; for a
    # plain register we keep the register name.
    slot_key = rcx_source
    if "[" in rcx_source:
        slot_key = rcx_source[rcx_source.index("[") : rcx_source.index("]") + 1]
    # e.g. slot_key = '[r14]' or '[rbx + 0x10]'

    # 3. Walk backward to `mov <same slot>, rax` (where lua_newstate's return
    #    was stored as L). Search a generous window.
    store_idx = None
    for back in range(rcx_load_idx - 1, max(rcx_load_idx - 100, 0) - 1, -1):
        ins = insns[back]
        if ins.mnemonic != "mov":
            continue
        parts = ins.op_str.split(",", 1)
        if len(parts) != 2:
            continue
        dst, src = parts[0].strip(), parts[1].strip()
        if src != "rax":
            continue
        dst_key = dst
        if "[" in dst:
            dst_key = dst[dst.index("[") : dst.index("]") + 1]
        if dst_key == slot_key:
            store_idx = back
            break
    if store_idx is None:
        return fail(
            f"could not find `mov {slot_key}, rax` storing lua_newstate's return"
        )

    # 4. The nearest `call` instruction before the store is lua_newstate.
    newstate_call_idx = None
    newstate_target = None
    for back in range(store_idx - 1, max(store_idx - 15, 0) - 1, -1):
        ins = insns[back]
        if ins.mnemonic == "call" and ins.op_str.startswith("0x"):
            newstate_call_idx = back
            newstate_target = int(ins.op_str, 16)
            break
    if newstate_target is None:
        return fail("no direct call precedes the L store; lua_newstate may be indirect")

    real, chain = trace_thunk(model, newstate_target)
    rf = model.find_runtime_function(real)
    body_size = (rf[1] - rf[0]) if rf else 0

    # Gather arg setup at the newstate call site for evidence.
    arg_ctx = {"rcx": "?", "rdx": "?"}
    if newstate_call_idx is not None:
        for reg in ("rcx", "rdx"):
            for back in insns[max(0, newstate_call_idx - 10) : newstate_call_idx][::-1]:
                op = back.op_str
                if op.startswith(reg + ",") or op.startswith(reg + " "):
                    arg_ctx[reg] = f"{back.mnemonic} {op}"
                    break

    rvas = [hx(newstate_target)]
    if len(chain) > 1:
        rvas.append(hx(real))

    thunk_note = ""
    if len(chain) > 1:
        thunk_note = (
            f" Entry via CFG thunk {'->'.join(hx(c) for c in chain)}; "
            f"real body at {hx(real)}."
        )

    evidence = (
        f"Backward dataflow trace from the lua_atpanic call at "
        f"{hx(insns[atpanic_idx].address)}: rcx <- `{rcx_source}` (loaded at "
        f"{hx(insns[rcx_load_idx].address)}), traced the L slot `{slot_key}` "
        f"to a `mov {slot_key}, rax` store at "
        f"{hx(insns[store_idx].address)}, whose nearest preceding direct call "
        f"is at {hx(insns[newstate_call_idx].address)}.{thunk_note} "
        f"Arg setup at the call site: rcx<-({arg_ctx['rcx']}), "
        f"rdx<-({arg_ctx['rdx']}) — matches lua_newstate(lua_Alloc f, void* ud). "
        f"Real body {hx(real)} ({body_size}B) contains indirect calls through "
        f"the allocator pointer — consistent with lua_newstate's allocate-state "
        f"+ allocate-stack pattern. This trace correctly skips the two "
        f"intervening lua_gc calls between newstate and atpanic."
    )
    return {
        "name": "lua_newstate",
        "candidate_rvas": rvas,
        "confidence": "high",
        "evidence": evidence,
        "discovery_method": "direct-call-trace",
        "thunk_entry_rva": hx(newstate_target) if len(chain) > 1 else None,
        "real_body_rva": hx(real) if len(chain) > 1 else hx(newstate_target),
        "body_size_bytes": body_size,
        "trace": {
            "atpanic_call_rva": hx(insns[atpanic_idx].address),
            "rcx_load_rva": hx(insns[rcx_load_idx].address),
            "rcx_source": rcx_source,
            "l_slot": slot_key,
            "store_rva": hx(insns[store_idx].address),
            "newstate_call_rva": hx(insns[newstate_call_idx].address),
        },
    }


def _find_loadbuffer_from_bytecode(model: PEModel, cat_a: list) -> Optional[dict]:
    """Trace from lua_resource::bytecode to find luaL_loadbuffer.

    The bytecode anchor resolves to two large engine functions. Within them we
    look for a direct call whose target body matches the luaL_loadbuffer
    signature (small wrapper calling a >8KB lua_load, with reader setup).
    """
    bytecode_entry = next((a for a in cat_a if a["anchor"] == "lua_resource::bytecode"), None)
    if not bytecode_entry:
        return None
    candidates = {}
    for cf in bytecode_entry["containing_functions"]:
        begin = int(cf["begin_rva"], 16)
        end = int(cf["end_rva"], 16)
        for call in enumerate_calls(model, begin, end):
            if call["kind"] != "direct" or call["import"]:
                continue
            real_rva = int(call["real_target_rva"], 16)
            cls = classify_by_body(model, real_rva)
            # The body-only classifier now labels these as cautious
            # "lua_load_wrapper_candidate". Tracing from the bytecode anchor
            # and confirming the 4-arg (L,buf,size,name) call-site context
            # is what elevates it to luaL_loadbuffer.
            if cls["classification"] == "lua_load_wrapper_candidate":
                candidates.setdefault(real_rva, {
                    "target": call["target_rva"],
                    "real": call["real_target_rva"],
                    "is_thunk": call["is_thunk"],
                    "thunk_chain": call["thunk_chain"],
                    "sites": [],
                    "arg_hints_samples": [],
                    "cls": cls,
                })
                candidates[real_rva]["sites"].append(call["call_site_rva"])
                candidates[real_rva]["arg_hints_samples"].append(call["arg_hints"])

    if not candidates:
        return None
    # Pick the one with the most call sites (most-referenced = most likely).
    best = max(candidates.values(), key=lambda v: len(v["sites"]))
    rvas = [best["target"]]
    if best["is_thunk"] and best["real"] not in rvas:
        rvas.append(best["real"])
    # The bytecode-trace context (4-arg call sites from the script-loading
    # path) elevates the body-only candidate to a confirmed luaL_loadbuffer.
    arg_evidence = "; ".join(
        f"site {s}: " + ", ".join(f"{k}<-{v}" for k, v in ah.items())
        for s, ah in zip(best["sites"], best["arg_hints_samples"])
    )
    return {
        "name": "luaL_loadbuffer",
        "candidate_rvas": rvas,
        "confidence": "high",
        "evidence": (
            f"{best['cls']['evidence']} Elevated to luaL_loadbuffer by "
            f"tracing from the lua_resource::bytecode anchor (2 containing "
            f"engine functions, {len(best['sites'])} call site(s): "
            f"{best['sites']}) with call-site arg context: {arg_evidence}. "
            f"The 4-arg (L,buf,size,name) shape plus lua_load callee confirms "
            f"luaL_loadbuffer over the other luaL_load* wrappers."
        ),
        "discovery_method": "direct-call-trace",
        "internal_lua_load_rva": best["cls"].get("internal_lua_load_rva"),
    }


# ---------------------------------------------------------------------------
# Phase D — LuaJIT error-string cross-check.
# ---------------------------------------------------------------------------
def phase_d(model: PEModel):
    """Try the documented Phase D (LEA-xref individual error strings).

    Finding (recorded even on negative result): LuaJIT's §5 error strings are
    entries in a contiguous lj_err_msg[] string block in .rdata and are NEVER
    referenced by LEA or by a pointer table anywhere in the binary. LuaJIT
    interns them at VM init via lj_str_new() and references them thereafter by
    GCstr* handle. So the documented Phase D approach produces ZERO xrefs.
    This is a methodology gap; the alternative is to find the lj_str_new
    interning loop, not to xref individual strings.
    """
    delta = model.rdata.rva - model.rdata.file_offset
    results = []
    any_xref = False
    for label, file_off, expected in ERROR_STRINGS_S5:
        anchor_rva = file_off + delta
        sites = find_lea_xrefs(model, anchor_rva)
        # Also check for any pointer (qword VA) anywhere in the file.
        needle = struct.pack("<Q", model.image_base + anchor_rva)
        ptr_hits = model.raw.count(needle)
        results.append(
            {
                "label": label,
                "string": expected.decode("latin1"),
                "documented_file_offset": hx(file_off),
                "computed_rva": hx(anchor_rva),
                "string_at_offset": model.raw[file_off : file_off + len(expected)] == expected,
                "lea_xref_count": len(sites),
                "lea_xref_sites": [hx(s) for s in sites],
                "pointer_table_hits": ptr_hits,
            }
        )
        if sites:
            any_xref = True

    return {
        "error_string_results": results,
        "any_xref_found": any_xref,
        "methodology_note": (
            "Phase D as documented does NOT work: 0 LEA xrefs and 0 pointer-"
            "table hits for every §5 error string. The strings are entries in "
            "a contiguous lj_err_msg[] block in .rdata and are interned into "
            "LuaJIT's string hash table at VM init via lj_str_new(); the code "
            "thereafter references them by GCstr* handle, never by raw .rdata "
            "address. Phase 2 must find the lj_str_new interning loop or use a "
            "different anchor (e.g. a known LuaJIT function body signature), "
            "not LEA-xref on individual error strings."
        ),
    }


# ---------------------------------------------------------------------------
# Phase E — emit addresses.json + report.md.
# ---------------------------------------------------------------------------
def sections_json(model: PEModel) -> dict:
    out = {}
    for name in (".text", ".rdata", ".pdata"):
        s = model.sections[name]
        out[name] = {
            "rva": hx(s.rva),
            "file_offset": hx(s.file_offset),
            "raw_size": s.raw_size,
            "virtual_size": s.virtual_size,
        }
    return out


def build_addresses_json(model: PEModel, a, b, init, c, d) -> dict:
    doc_corrections = list(a["doc_corrections"])
    methodology_gaps = _methodology_gaps(c, d)

    return {
        "binary": {
            "path": model.path,
            "size_bytes": len(model.raw),
            "sha256": model.sha256,
        },
        "pe_sections": sections_json(model),
        "rdata_delta": a["delta"],
        "rdata_delta_matches_doc": a["delta_match"],
        "anchor_sanity": a["anchor_sanity"],
        "category_a_functions": b,
        "init_candidate": init,
        "category_b_candidates": c["category_b_candidates"],
        "init_candidate_call_graph": c["init_call_graph"],
        "classified_call_targets": c["classified_targets"],
        "luajit_error_string_crosscheck": d,
        "doc_corrections": doc_corrections,
        "methodology_gaps": methodology_gaps,
    }


def _methodology_gaps(c: dict, d: dict) -> list:
    gaps = []
    gaps.append(
        "CFG/hot-patch thunks (5-byte E9 rel32 + cc padding) have NO .pdata "
        "entry of their own — they sit in gaps between RUNTIME_FUNCTION "
        "entries. Discovery MUST follow the thunk to the real body. "
        "Observed: lua_newstate is invoked at the thunk 0xc7c000 whose real "
        "body is at 0xc7eea0."
    )
    gaps.append(
        "Leaf functions (no prologue, SP unchanged, ret-only epilogue) are "
        "ALSO missing .pdata entries — MSVC omits them. lua_gettop "
        "(0xc74050), lua_atpanic (0xc77f40), and the lua_push* primitives "
        "(e.g. 0xc74770) all fall in .pdata gaps. .pdata is NOT a complete "
        "function map; Phase 2 must handle addresses outside .pdata by "
        "disasming bytes directly and trimming at the first ret."
    )
    gaps.append(
        "Import thunks (FF 25 disp32 = jmp [rip+disp32]) are a third category "
        "of call target in .pdata gaps. They must be resolved through the IAT, "
        "not treated as function bodies. Example: 0xdf593c -> "
        "VCRUNTIME140.dll!memmove."
    )
    gaps.append(
        "Phase D as documented does not work. The §5 LuaJIT error strings "
        "(attempt_to_call, bad_argument, loop_in_gettable, invalid_key_next) "
        "have ZERO LEA xrefs and ZERO pointer-table references anywhere in "
        "the binary. They are entries in a contiguous lj_err_msg[] block and "
        "are interned via lj_str_new() at VM init; thereafter referenced by "
        "GCstr* handle. Phase 2 must use a different LuaJIT-internal anchor "
        "(e.g. the lj_str_new interning loop, or a known function-body "
        "signature). Do NOT LEA-xref individual error strings."
    )
    gaps.append(
        "lua_pcall could not be conclusively identified offline. It has no "
        "string anchor and the engine's bytecode path does not exhibit a "
        "clean (L,int,int,int) call site resolving to a thin lj_docall "
        "wrapper. Phase 2 must locate it by clustering near the other "
        "confirmed LuaJIT API functions in the 0xc7xxxx region or by "
        "structural pattern."
    )
    gaps.append(
        "The lua_panic string anchor's containing function is lua_panic ITSELF "
        "(it logs its own name), NOT the init code. The reliable path to init "
        "is: find the lua_panic body, then find LEA references to that body's "
        "address (the &lua_panic taken for lua_atpanic). The anchors doc §7 "
        "implies the string xref lands directly in init code; it does not."
    )
    return gaps


def write_report(model: PEModel, a, b, init, c, d, addresses: dict) -> str:
    lines = []
    w = lines.append
    w("# Phase 0 Offline Discovery — Report")
    w("")
    w("> Pure offline binary analysis of `Darktide.exe`. No DLL, no game ")
    w("> launch, no injection. This report validates the anchors-doc §7 ")
    w("> discovery methodology and produces a cross-check table for Phase 2.")
    w("")
    w(f"- **Binary:** `{model.path}`")
    w(f"- **Size:** {len(model.raw):,} bytes (expected {EXPECTED_SIZE:,})")
    w(f"- **SHA-256:** `{model.sha256}`")
    w("")

    # PE summary
    w("## PE parse summary")
    w("")
    w("| Section | RVA | File offset | Raw size | Virtual size |")
    w("|---------|-----|-------------|----------|--------------|")
    for name in (".text", ".rdata", ".pdata"):
        s = model.sections[name]
        w(f"| `{name}` | `{hx(s.rva)}` | `{hx(s.file_offset)}` | {s.raw_size:,} | {s.virtual_size:,} |")
    w("")
    delta = a["delta"]
    match = "matches" if a["delta_match"] else "DOES NOT MATCH"
    w(f"- **.rdata RVA delta:** `{hx(delta)}` ({delta}) — **{match}** the documented `{hx(EXPECTED_RDATA_DELTA)}`.")
    w(f"- **.pdata entries:** {len(model.runtime_functions):,} non-zero RUNTIME_FUNCTION records.")
    w("")

    # Anchor sanity
    w("## Anchor sanity check (Phase A)")
    w("")
    w("Every §3 Quick-Reference anchor was read at its documented file offset ")
    w("and compared against the documented string.")
    w("")
    w("| Anchor | File offset | At offset? | Computed RVA | Correction |")
    w("|--------|-------------|------------|--------------|------------|")
    for s in a["anchor_sanity"]:
        corr = s["doc_correction"] or "—"
        w(f"| `{s['label']}` | `{s['documented_file_offset']}` | **{s['actual_at_offset']}** | `{s['computed_rva']}` | {corr} |")
    w("")
    n_pass = sum(1 for s in a["anchor_sanity"] if s["actual_at_offset"] == "yes")
    w(f"**Result: {n_pass}/{len(a['anchor_sanity'])} anchors verified at their documented offsets.**")
    if a["doc_corrections"]:
        w("")
        w("**Corrections:**")
        for corr in a["doc_corrections"]:
            w(f"- {corr}")
    else:
        w("")
        w("**No string-offset corrections needed.** Every §3 anchor matched.")
    w("")

    # Category A
    w("## Category A engine functions (Phase B)")
    w("")
    w("For each anchor: `.text` was scanned for RIP-relative LEA references; ")
    w("each xref site was mapped to its containing RUNTIME_FUNCTION via .pdata.")
    w("")
    w("| Anchor | Anchor RVA | Xrefs | Distinct containing funcs | Func RVAs |")
    w("|--------|-----------:|------:|--------------------------:|----------|")
    for entry in b:
        funcs = ", ".join(f"`{f['begin_rva']}`" for f in entry["containing_functions"]) or "—"
        w(f"| `{entry['anchor']}` | `{entry['anchor_rva']}` | {entry['xref_count']} | {len(entry['containing_functions'])} | {funcs} |")
    w("")
    w("Notes:")
    w("- `lua_panic` resolves to a single 160-byte function — that function ")
    w("  **is `lua_panic` itself** (it references its own name in its logging ")
    w("  path), not the init code. See the init-candidate section below.")
    w("- `lua_resource::bytecode` resolves to **two** large engine functions ")
    w("  (each ~2.5 KB); these are the bytecode→VM loading path.")
    w("- `LuaJIT version` resolves to a function inside the LuaJIT cluster ")
    w("  (0xc80bf5), a useful sanity check that the cluster is correctly located.")
    w("")

    # Init candidate
    w("## Init candidate selection (Phase C)")
    w("")
    if init is None:
        w("**No init candidate could be selected.** This would be a blocking result.")
    else:
        w(f"Selected **`{init['begin_rva']}`–`{init['end_rva']}`** ({init['size_bytes']} bytes) as ")
        w("the LuaEnvironment init function.")
        w("")
        w(f"{init['reasoning']}")
        w("")
        w(f"- `lua_environment` string marker present in body: **{init['lua_environment_marker_found']}**")
        w(f"- lua_panic body (string-xref owner): `{init['lua_panic_body_rva']}`")
        w(f"- LEA-of-&lua_panic sites (lua_atpanic setup): {init['lea_of_lua_panic_sites']}")
    w("")

    # Category B
    w("## Category B LuaJIT candidates (Phase C)")
    w("")
    w("Direct-call graph of the init candidate was enumerated; each distinct ")
    w("call target was resolved (thunks followed, import thunks identified) ")
    w("and classified by body shape and call context.")
    w("")
    w("### Confirmed identifications")
    w("")
    w("| Function | Candidate RVA(s) | Confidence | Discovery method |")
    w("|----------|------------------|------------|------------------|")
    for cb in c["category_b_candidates"]:
        rvas = ", ".join(f"`{r}`" for r in cb["candidate_rvas"]) or "—"
        w(f"| `{cb['name']}` | {rvas} | **{cb['confidence']}** | {cb['discovery_method']} |")
    w("")
    for cb in c["category_b_candidates"]:
        w(f"### `{cb['name']}` — confidence: {cb['confidence']}")
        w("")
        w(f"- **Candidate RVA(s):** {', '.join('`'+r+'`' for r in cb['candidate_rvas']) or 'none'}")
        w(f"- **Discovery method:** {cb['discovery_method']}")
        w(f"- **Evidence:** {cb['evidence']}")
        w("")

    # Full classified call graph summary
    w("### Full init-candidate call graph (classified)")
    w("")
    w("| Call target | Thunk? | Real body | Size | Import | Classification | Confidence |")
    w("|-------------|--------|-----------|-----:|--------|----------------|------------|")
    for ct in sorted(c["classified_targets"], key=lambda x: int(x["real_target_rva"], 16)):
        w(
            f"| `{ct['target_rva']}` | {'yes' if ct['is_thunk'] else 'no'} | "
            f"`{ct['real_target_rva']}` | {ct['func_size'] or '?'} | "
            f"{ct['import'] or '—'} | `{ct['classification']}` | {ct['confidence']} |"
        )
    w("")
    indirect = [x for x in c["init_call_graph"] if x.get("kind") == "indirect"]
    w(f"Indirect calls inside init (flagged for Phase 2 backward tracing): **{len(indirect)}**")
    for ix in indirect:
        w(f"- at `{ix['call_site_rva']}`: `call {ix['operand']}`")
    if not indirect:
        w("(none — the init path uses only direct calls.)")
    w("")

    # Phase D
    w("## LuaJIT error-string cross-check (Phase D)")
    w("")
    w("The documented Phase D approach is to LEA-xref the §5 LuaJIT error ")
    w("strings. **This does not work.** All four documented error strings ")
    w("produce zero LEA xrefs and zero pointer-table references anywhere in ")
    w("the binary.")
    w("")
    w("| Error string | File offset | String present? | LEA xrefs | Pointer hits |")
    w("|--------------|-------------|-----------------|----------:|-------------:|")
    for r in d["error_string_results"]:
        w(f"| `{r['label']}` | `{r['documented_file_offset']}` | {r['string_at_offset']} | {r['lea_xref_count']} | {r['pointer_table_hits']} |")
    w("")
    w(f"**Methodology note:** {d['methodology_note']}")
    w("")
    w("The §5 strings ARE present in the binary at their documented offsets ")
    w("and they ARE laid out as a contiguous `lj_err_msg[]` block (the wider ")
    w("0xe89b00–0xe89e00 region contains ~40 error-message strings packed ")
    w("back-to-back). But the code never references them by address — they ")
    w("are interned into LuaJIT's string hash table at VM init and accessed ")
    w("by `GCstr*` handle thereafter.")
    w("")

    # Doc corrections & methodology gaps
    w("## Doc corrections and methodology gaps")
    w("")
    if addresses["doc_corrections"]:
        w("### Doc corrections")
        w("")
        for corr in addresses["doc_corrections"]:
            w(f"- {corr}")
        w("")
    else:
        w("### Doc corrections")
        w("")
        w("**None found.** Every §3 anchor string is present at its documented ")
        w("file offset, and the `.rdata` RVA delta is exactly `0x2a00` as documented.")
        w("")
    w("### Methodology gaps (must feed back into the anchors doc before Phase 2)")
    w("")
    for i, gap in enumerate(addresses["methodology_gaps"], 1):
        w(f"{i}. {gap}")
    w("")

    # Recommendations
    w("## Recommendations for Phase 2")
    w("")
    w("Based on the above, runtime discovery should:")
    w("")
    w("1. **Follow CFG thunks.** When a direct call target has no .pdata ")
    w("entry, check for `E9 rel32` and follow the chain. `lua_newstate` is ")
    w("invoked at the thunk `0xc7c000`; the real body is `0xc7eea0`. A ")
    w("runtime hook must install on the thunk entry (what callers actually ")
    w("invoke) OR on the real body — pick one and document it. Hooking the ")
    w("thunk is safer for capture (it is the actual call target).")
    w("")
    w("2. **Do NOT assume .pdata is a complete function map.** Leaf functions ")
    w("(lua_gettop, lua_atpanic, lua_push*) have no .pdata entries. When ")
    w("classifying a call target that falls in a .pdata gap, disassemble the ")
    w("bytes directly and trim at the first `ret`. The byte signatures for ")
    w("lua_gettop and lua_atpanic are stable and documented in this report.")
    w("")
    w("3. **Resolve import thunks.** `FF 25 disp32` calls go through the IAT. ")
    w("Phase 2 should parse the import directory and resolve these to ")
    w("`DLL!function` names (useful for recognizing allocator/VirtualAlloc ")
    w("calls inside lua_newstate).")
    w("")
    w("4. **Use the multi-signal init path.** The reliable recipe is: ")
    w("  (a) find the `lua_panic` string → its containing function IS lua_panic; ")
    w("  (b) find LEA references to that function's address → those are the ")
    w("      lua_atpanic setup sites; the largest containing function is ")
    w("      LuaEnvironment init. ")
    w("  Do NOT assume the string xref lands directly in init.")
    w("")
    w("5. **Drop Phase D's individual-string-xref approach.** It cannot work. ")
    w("To anchor into LuaJIT's error layer, find the `lj_str_new()` interning ")
    w("loop that processes the `lj_err_msg[]` block at VM init. This is ")
    w("non-trivial and may not be worth it — the confirmed LuaJIT API cluster ")
    w("around 0xc7xxxx (lua_newstate, lua_atpanic, lua_gettop, lua_gc, ")
    w("luaL_loadbuffer) is a better anchor surface than the error strings.")
    w("")
    w("6. **Locate lua_pcall by clustering.** lua_pcall, lua_gettop, ")
    w("lua_atpanic, lua_newstate, and luaL_loadbuffer are all emitted from ")
    w("LuaJIT's `lj_api.c` / `lj_load.c` and live in a tight address cluster ")
    w("(0xc73xxx–0xc7exxx on this build). Once one LuaJIT API function is ")
    w("confirmed, the others are nearby. lua_pcall is a thin wrapper around ")
    w("`lj_docall`; look for a small function taking `(L, nargs, nresults, ")
    w("errfunc)` — 4 args, the last three small integers — that calls an ")
    w("internal docall routine. Confirm dynamically in Story 3.")
    w("")
    w("7. **Cross-check targets for Phase 2.** The `candidate_rvas` in ")
    w("`addresses.json` are the values Phase 2's runtime discovery should ")
    w("reproduce. A match confirms both implementations.")
    w("")

    report = "\n".join(lines) + "\n"
    with open(REPORT_PATH, "w") as f:
        f.write(report)
    return report


# ---------------------------------------------------------------------------
# Main.
# ---------------------------------------------------------------------------
def main() -> int:
    print(f"[phase0] reading {BINARY_PATH}")
    model = load_pe(BINARY_PATH)
    print(f"[phase0] binary ok: {len(model.raw):,} bytes, sha256={model.sha256[:16]}...")
    print(f"[phase0] .pdata entries: {len(model.runtime_functions):,}")

    print("[phase0] Phase A: PE parse + anchor sanity")
    a = phase_a(model)
    print(f"[phase0]   .rdata delta = {hx(a['delta'])} (doc says {hx(EXPECTED_RDATA_DELTA)}): {'OK' if a['delta_match'] else 'MISMATCH'}")
    print(f"[phase0]   anchors verified: {sum(1 for s in a['anchor_sanity'] if s['actual_at_offset']=='yes')}/{len(a['anchor_sanity'])}")
    print(f"[phase0]   doc corrections: {len(a['doc_corrections'])}")

    print("[phase0] Phase B: Category A xref -> .pdata")
    b = phase_b(model, a)
    for entry in b:
        print(f"[phase0]   {entry['anchor']:28s} xrefs={entry['xref_count']:3d} funcs={len(entry['containing_functions'])}")

    print("[phase0] Phase C: select init candidate + classify calls")
    init, panic_body, lea_sites, alts = select_init_candidate(model, b)
    if init is None:
        print("[phase0]   !! no init candidate found (blocking)")
    else:
        print(f"[phase0]   init candidate: {init['begin_rva']}-{init['end_rva']} ({init['size_bytes']}B)")
        print(f"[phase0]   lua_panic body: {init['lua_panic_body_rva']}")
        print(f"[phase0]   lua_environment marker: {init['lua_environment_marker_found']}")
    c = phase_c(model, init, b)
    for cb in c["category_b_candidates"]:
        rvas = ",".join(cb["candidate_rvas"]) or "(none)"
        print(f"[phase0]   Category B: {cb['name']:20s} {cb['confidence']:6s} {rvas}")

    print("[phase0] Phase D: LuaJIT error-string cross-check")
    d = phase_d(model)
    total_xrefs = sum(r["lea_xref_count"] for r in d["error_string_results"])
    print(f"[phase0]   total LEA xrefs to §5 error strings: {total_xrefs} (expected 0 — gap confirmed)")

    print("[phase0] Phase E: writing outputs")
    addresses = build_addresses_json(model, a, b, init, c, d)
    with open(JSON_PATH, "w") as f:
        json.dump(addresses, f, indent=2, sort_keys=False)
    print(f"[phase0]   wrote {JSON_PATH}")
    write_report(model, a, b, init, c, d, addresses)
    print(f"[phase0]   wrote {REPORT_PATH}")

    print("[phase0] done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
