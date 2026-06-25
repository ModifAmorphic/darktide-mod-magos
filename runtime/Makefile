# Makefile — Component A mixed DLL build.
#
# Reproducible MinGW cross-compile from Linux: Rust staticlib + C shell/MinHook
# -> one PE DLL (magos_shell.dll), plus the launcher exe. The MSVC-native build
# runs in CI (.github/workflows/msvc.yml) on a Windows runner.
#
# This Makefile lives in runtime/, so every path below is relative to runtime/.
# ALL build outputs land in bin/ (gitignored); cargo's target/ also resolves
# here (runtime/target/).
#
# Usage (from runtime/):
#   make build          # cross-compile the DLL + launcher (x86_64-pc-windows-gnu)
#   make check          # verify the DLL is a valid PE with a DllMain entry
#   make test           # cargo test + C unit tests (wine) + Enginseer Lua tests
#   make clean

CARGO     ?= cargo
CROSS_CC  ?= x86_64-w64-mingw32-gcc
OBJDUMP   ?= x86_64-w64-mingw32-objdump
NM        ?= x86_64-w64-mingw32-nm
TARGET    := x86_64-pc-windows-gnu
PROFILE   := release
WINE      ?= wine

BIN := bin

REL_DIR   := target/$(TARGET)/$(PROFILE)
RUST_LIB  := $(REL_DIR)/libmagos_discovery.a
# Rust sources the staticlib depends on. GNU make's `**` is not recursive (it
# expands to nothing here), so list src/*.rs explicitly. discovery/tests/*.rs
# are separate integration-test binaries, not compiled into the staticlib.
RUST_SRCS := $(wildcard discovery/src/*.rs)
DLL       := $(BIN)/magos_shell.dll
LAUNCHER  := $(BIN)/magos_launcher.exe

SHELL_SRC := shell/src/dllmain.c shell/src/trampoline.c
MINHOOK_SRC := shell/vendor/minhook/src/buffer.c \
               shell/vendor/minhook/src/hook.c \
               shell/vendor/minhook/src/trampoline.c \
               shell/vendor/minhook/src/hde/hde64.c
INCLUDES  := -I shell/include -I shell/vendor/minhook/include

# Rust std's Windows system-lib dependencies (mingw).
RUST_SYS  := -lpsapi -lkernel32 -luser32 -lws2_32 -lbcrypt -luserenv -lntdll -lgcc

# ---- C test infrastructure ----
TEST_CC       := $(CROSS_CC)
TEST_CFLAGS   := -static-libgcc -O2
TEST_INCLUDES := -I launcher/src -I shell/include
TEST_LIBS     := -lkernel32

TEST_RUNNER_OBJ := $(BIN)/test_runner.o
LAUNCHER_OBJ    := $(BIN)/launcher.o

# Stub executables for injection testing (built into bin/ alongside test exes)
STUB_TARGET := $(BIN)/stub_target.exe
STUB_SHELL  := $(BIN)/stub_shell.dll

# Test executables
TEST_EXES := $(BIN)/test_steam_env.exe $(BIN)/test_injection.exe \
             $(BIN)/test_config.exe $(BIN)/test_trampoline.exe

.PHONY: all build dll launcher check test c-tests enginseer-test clean rust-staticlib

all: build

# Ensure the output directory exists before any rule writes into it (order-only
# prereq of every output-producing rule below).
$(BIN):
	mkdir -p $@

rust-staticlib: $(RUST_LIB)
$(RUST_LIB): $(RUST_SRCS) discovery/Cargo.toml Cargo.toml Cargo.lock
	$(CARGO) build --$(PROFILE) -p magos-discovery --target $(TARGET)

build: dll launcher

dll: $(DLL)
$(DLL): $(RUST_LIB) $(SHELL_SRC) $(MINHOOK_SRC) shell/include/magos_discovery.h shell/include/trampoline.h | $(BIN)
	$(CROSS_CC) -shared -static-libgcc -O2 -o $@ \
	  $(SHELL_SRC) $(MINHOOK_SRC) $(INCLUDES) \
	  -l:libmagos_discovery.a -L $(REL_DIR) \
	  $(RUST_SYS) -Wl,--out-implib,$(BIN)/magos_shell.lib

launcher: $(LAUNCHER)
$(LAUNCHER): launcher/src/launcher.c launcher/src/launcher.h | $(BIN)
	$(CROSS_CC) -static-libgcc -O2 -o $@ launcher/src/launcher.c -lkernel32

check: $(DLL)
	@echo "--- file type ---"; file $(DLL)
	@echo "--- entry point ---"; $(OBJDUMP) -p $(DLL) | grep -i "AddressOfEntryPoint"
	@echo "--- DllMain symbol ---"; $(NM) $(DLL) | grep -iE " T DllMain$$"
	@echo "--- Rust seam symbols ---"; $(NM) $(DLL) | grep -iE " T magos_(discover|test_panic)"
	@echo "--- runtime DLL deps (no magos_discovery.dll / libgcc) ---"; $(OBJDUMP) -p $(DLL) | grep "DLL Name"
	@test -n "$$($(NM) $(DLL) | grep -iE ' T DllMain$$')" && echo "CHECK PASS: valid PE DLL with DllMain" || (echo "CHECK FAIL"; exit 1)

# ---- C test build rules ----

$(TEST_RUNNER_OBJ): tests/test_runner.c tests/test_runner.h | $(BIN)
	$(TEST_CC) $(TEST_CFLAGS) -c -o $@ $<

$(LAUNCHER_OBJ): launcher/src/launcher.c launcher/src/launcher.h | $(BIN)
	$(TEST_CC) $(TEST_CFLAGS) $(TEST_INCLUDES) -DMAGOS_TEST_BUILD -c -o $@ $<

# Stub target — minimal Windows GUI exe that sleeps then exits 0
$(STUB_TARGET): tests/stub_target.c | $(BIN)
	$(TEST_CC) $(TEST_CFLAGS) -mwindows -o $@ $<

# Stub shell — minimal DLL that signals magos_hook_ready on attach
$(STUB_SHELL): tests/stub_shell.c | $(BIN)
	$(TEST_CC) $(TEST_CFLAGS) -shared -o $@ $<

$(BIN)/test_steam_env.exe: tests/test_steam_env.c tests/test_runner.h \
                          $(TEST_RUNNER_OBJ) $(LAUNCHER_OBJ) | $(BIN)
	$(TEST_CC) $(TEST_CFLAGS) $(TEST_INCLUDES) -o $@ $< \
	  $(TEST_RUNNER_OBJ) $(LAUNCHER_OBJ) $(TEST_LIBS)

$(BIN)/test_injection.exe: tests/test_injection.c tests/test_runner.h \
                          $(TEST_RUNNER_OBJ) $(LAUNCHER_OBJ) | $(BIN)
	$(TEST_CC) $(TEST_CFLAGS) $(TEST_INCLUDES) -o $@ $< \
	  $(TEST_RUNNER_OBJ) $(LAUNCHER_OBJ) $(TEST_LIBS)

$(BIN)/test_config.exe: tests/test_config.c tests/test_runner.h \
                          $(TEST_RUNNER_OBJ) $(LAUNCHER_OBJ) | $(BIN)
	$(TEST_CC) $(TEST_CFLAGS) $(TEST_INCLUDES) -DMAGOS_TEST_BUILD -o $@ $< \
	  $(TEST_RUNNER_OBJ) $(LAUNCHER_OBJ) $(TEST_LIBS)

# test_trampoline compiles the pure trampoline.c inline (via #include), so it
# needs only the test runner + kernel32 (no Lua/hook deps).
$(BIN)/test_trampoline.exe: tests/test_trampoline.c \
                          shell/src/trampoline.c shell/include/trampoline.h \
                          tests/test_runner.h $(TEST_RUNNER_OBJ) | $(BIN)
	$(TEST_CC) $(TEST_CFLAGS) $(TEST_INCLUDES) -o $@ $< \
	  $(TEST_RUNNER_OBJ) $(TEST_LIBS)

c-tests: $(TEST_EXES) $(STUB_TARGET) $(STUB_SHELL)
	@echo "=== C unit tests (via wine) ==="
	$(WINE) $(BIN)/test_steam_env.exe
	$(WINE) $(BIN)/test_injection.exe
	$(WINE) $(BIN)/test_config.exe
	$(WINE) $(BIN)/test_trampoline.exe

test: c-tests enginseer-test
	$(CARGO) test --features test-hooks -p magos-discovery

# ---- Enginseer Lua tests (offline LuaJIT harness) ----
# Runs the v2 Enginseer loader's offline test harness via luajit. Pure-Lua;
# no game binary or wine needed. Wired into `test` (runs alongside the C +
# Rust tests) AND kept as a standalone target for fast Lua-only iteration.
# Requires /usr/bin/luajit (LuaJIT 2.1); if absent, the target fails clearly.
LUAJIT ?= luajit
ENGINSER_TESTS := enginseer/tests/runner.lua

enginseer-test:
	@echo "=== Enginseer Lua tests (LuaJIT) ==="
	$(LUAJIT) $(ENGINSER_TESTS)

clean:
	$(CARGO) clean
	rm -rf $(BIN)
