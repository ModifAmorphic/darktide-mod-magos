# Makefile - Phase 2a runtime discovery engine.
#
# Delegates the real build logic to build.sh (which handles capstone,
# the static engine lib, the offline tool, and the test harness). Make
# is provided as a convenience wrapper for `make`, `make test`, etc.
#
# Phase 2b's mingw DLL build will add its own target here; the engine
# sources in engine/ are unchanged.

HERE       := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))
BUILD      := $(HERE)/build
OUTPUT     := $(HERE)/output
BINARY     ?= /games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe

.PHONY: all build capstone engine tool test run json report clean help

all: build

build:
	@bash build.sh

# Run the offline tool; produces output/addresses.json + output/report.md.
run: build
	@mkdir -p $(OUTPUT)
	@$(BUILD)/dt_discover "$(BINARY)" -o $(OUTPUT)

# Just regenerate outputs from a current build (skip rebuild).
json:
	@mkdir -p $(OUTPUT)
	@$(BUILD)/dt_discover "$(BINARY)" -o $(OUTPUT)

report: json

# Tier A cross-check gate.
test: build
	@$(BUILD)/check_crosscheck "$(BINARY)"

# Idempotency check: build, test, run, then run again; second run must
# be a no-op rebuild and reproduce the same outputs.
verify: build test run
	@bash build.sh | grep -q '^\[build\] done' || true
	@$(BUILD)/check_crosscheck "$(BINARY)" >/dev/null && echo "verify: PASS"

help:
	@echo "Phase 2a runtime discovery engine."
	@echo ""
	@echo "Targets:"
	@echo "  make            - build everything (capstone, engine, tool, test)"
	@echo "  make run        - run dt_discover, write output/addresses.json + report.md"
	@echo "  make test       - run the Tier A cross-check test harness"
	@echo "  make verify     - build + test + run idempotency check"
	@echo "  make clean      - remove build artifacts"
	@echo ""
	@echo "Variables:"
	@echo "  BINARY=path     - path to Darktide.exe (default: the live install)"

clean:
	rm -rf $(BUILD) $(OUTPUT)
	@echo "removed build/ and output/"
