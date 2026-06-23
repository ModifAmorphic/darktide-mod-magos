# Makefile - Phase 5 DMF bootstrap DLL + tests.
#
# Delegates the DLL cross-build to build.sh. Adds Tier A test targets
# (A1 = mock-VM injection + DMF bootstrap, A2 = plumbing smoke).

HERE       := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))
BUILD      := $(HERE)/build
TEST_BUILD := $(HERE)/test/build

.PHONY: all build dll a1 a2 verify clean help

all: build

build: dll

# Cross-compile the dbghelp.dll (Phase 1 + Phase 2b engine + Phase 3 hooks
# (observers OFF) + Phase 4 inject + Phase 5 DMF bootstrap).
dll:
	@bash build.sh

# A1: mock-VM injection + DMF bootstrap test — the STRONG GATE. Compiles
# inject.c with -DPHASE4_TEST_API against the system LuaJIT and exercises
# the exact detour logic against a real LuaJIT VM. Linux native (no Wine).
a1:
	@bash test/run_a1_inject_test.sh

# A2: DLL plumbing smoke test under Wine.
a2:
	@bash test/run_a2_smoke.sh

# Full Tier A verification: DLL + A1 + A2.
verify: dll a1 a2
	@echo "==========================================="
	@echo "Phase 5 verify: ALL PASS"
	@echo "==========================================="

help:
	@echo "Phase 5 DMF bootstrap DLL + tests."
	@echo ""
	@echo "Targets:"
	@echo "  make build    - cross-compile build/dbghelp.dll (mingw)"
	@echo "  make a1       - Tier A1: mock-VM DMF bootstrap against real LuaJIT (strong gate)"
	@echo "  make a2       - Tier A2: DLL plumbing smoke (Wine)"
	@echo "  make verify   - build + a1 + a2"
	@echo "  make clean    - remove build artifacts"
	@echo ""

clean:
	rm -rf $(BUILD) $(TEST_BUILD)
	@echo "removed build/ and test/build/"
