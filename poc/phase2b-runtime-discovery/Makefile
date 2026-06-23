# Makefile - Phase 2b runtime discovery DLL + tests.
#
# Delegates the DLL cross-build to build.sh. Adds native (gcc) targets for
# the IAT-corruption regression test (should-fix #5), which mirrors
# check_crosscheck.c's build path and links the engine statically.

HERE       := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))
P2A_DIR    := $(HERE)/../phase2-runtime-discovery
BUILD      := $(HERE)/build
TEST_BUILD := $(HERE)/test/build
ENGINE     := $(P2A_DIR)/engine
VENDOR     := $(P2A_DIR)/vendor/capstone
BINARY     ?= /games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe

CC      ?= gcc
CFLAGS  ?= -std=c11 -O2 -Wall -Wextra -Wno-unused-function \
           -Wno-unused-but-set-variable -Wno-format-truncation

.PHONY: all build dll test iat-test a1 a2 verify clean help

all: build

build: dll

# Cross-compile the dbghelp.dll (Phase 1 forwarding + Phase 2b engine).
dll:
	@bash build.sh

# A1: in-memory engine correctness (the strong gate).
a1:
	@bash test/run_in_memory_test.sh

# A2: DLL plumbing smoke test.
a2:
	@bash test/run_wine_test.sh

# IAT-corruption regression (should-fix #5). Native gcc build.
iat-test: $(TEST_BUILD)/corrupt_iat_test
	@echo "[iat] running against $(BINARY)..."
	@$(TEST_BUILD)/corrupt_iat_test "$(BINARY)"

# Phase 2a's verify must still pass after the should-fixes (run there).
p2a-verify:
	@$(MAKE) -C $(P2A_DIR) verify

# Full verification: DLL + all Tier A tests + Phase 2a regression.
verify: dll a2 a1 iat-test p2a-verify
	@echo "==========================================="
	@echo "Phase 2b verify: ALL PASS"
	@echo "==========================================="

# ---- native build of the IAT test ------------------------------------
ENGINE_SRCS := \
	$(ENGINE)/pe.c $(ENGINE)/scan.c $(ENGINE)/disasm.c \
	$(ENGINE)/classify.c $(ENGINE)/lua_pcall.c $(ENGINE)/discover.c \
	$(ENGINE)/json.c $(ENGINE)/report.c $(ENGINE)/sha256.c \
	$(ENGINE)/anchors.c

CAPSTONE_LIB := $(VENDOR)/build/libcapstone.a

$(CAPSTONE_LIB):
	@$(MAKE) -C $(P2A_DIR) build > /dev/null

$(TEST_BUILD)/corrupt_iat_test: test/corrupt_iat_test.c $(ENGINE_SRCS) $(CAPSTONE_LIB)
	@mkdir -p $(TEST_BUILD)
	$(CC) $(CFLAGS) -I$(ENGINE) -I$(VENDOR)/include \
	    test/corrupt_iat_test.c $(ENGINE_SRCS) \
	    $(CAPSTONE_LIB) -lm -o $@
	@echo "[iat] built $@"

help:
	@echo "Phase 2b runtime discovery DLL + tests."
	@echo ""
	@echo "Targets:"
	@echo "  make build      - cross-compile build/dbghelp.dll (mingw)"
	@echo "  make a1         - Tier A1: in-memory engine correctness (Wine)"
	@echo "  make a2         - Tier A2: DLL plumbing smoke test (Wine)"
	@echo "  make iat-test   - IAT-corruption regression (should-fix #5)"
	@echo "  make p2a-verify - re-run Phase 2a make verify (must stay 28/28)"
	@echo "  make verify     - build + a2 + a1 + iat-test + p2a-verify"
	@echo "  make clean      - remove build artifacts"
	@echo ""
	@echo "Variables:"
	@echo "  BINARY=path     - path to Darktide.exe (default: the live install)"

clean:
	rm -rf $(BUILD) $(TEST_BUILD)
	@echo "removed build/ and test/build/"
