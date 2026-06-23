# Makefile - Phase 3 state-capture DLL + tests.
#
# Delegates the DLL cross-build to build.sh. Adds Tier A test targets
# (A1 = hook mechanism, A2 = plumbing smoke, disasm = candidate analysis).

HERE       := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))
P2B_DIR    := $(HERE)/../phase2b-runtime-discovery
BUILD      := $(HERE)/build
TEST_BUILD := $(HERE)/test/build

.PHONY: all build dll minhook a1 a2 disasm verify clean help

all: build

build: dll

# Cross-compile the dbghelp.dll (Phase 1 + Phase 2b engine + Phase 3 hooks).
dll:
	@bash build.sh

# Build only MinHook (used by A1 in isolation).
minhook:
	@bash build.sh --minhook-only

# A1: MinHook hook mechanism end-to-end (the meaningful test).
a1:
	@bash test/run_a1_hook_test.sh

# A2: DLL plumbing smoke test.
a2:
	@bash test/run_a2_smoke.sh

# Disassemble the 3 lua_pcall candidates + the pruned one.
disasm:
	@bash tool/run_disasm.sh

# Full Tier A verification: DLL + MinHook + A1 + A2 + disasm.
verify: dll a1 a2 disasm
	@echo "==========================================="
	@echo "Phase 3 verify: ALL PASS"
	@echo "==========================================="

help:
	@echo "Phase 3 state-capture DLL + tests."
	@echo ""
	@echo "Targets:"
	@echo "  make build    - cross-compile build/dbghelp.dll (mingw)"
	@echo "  make minhook  - build build/minhook.a only"
	@echo "  make a1       - Tier A1: MinHook hook+capture+passthrough (Wine)"
	@echo "  make a2       - Tier A2: DLL plumbing smoke (Wine)"
	@echo "  make disasm   - disassemble the 4 lua_pcall candidates"
	@echo "  make verify   - build + a1 + a2 + disasm"
	@echo "  make clean    - remove build artifacts"
	@echo ""

clean:
	rm -rf $(BUILD) $(TEST_BUILD)
	@echo "removed build/ and test/build/"
