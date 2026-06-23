# Makefile — Component A mixed DLL build (Spike 001 step 1).
#
# Reproducible MinGW cross-compile from Linux: Rust staticlib + C shell/MinHook
# -> one PE DLL (magos_shell.dll), plus the launcher exe. The MSVC-native build
# runs in CI (.github/workflows/msvc.yml) on a Windows runner.
#
# Usage:
#   make build          # cross-compile the DLL + launcher (x86_64-pc-windows-gnu)
#   make check          # verify the DLL is a valid PE with a DllMain entry
#   make test           # cargo test the discovery crate (host)
#   make clean

CARGO     ?= cargo
CROSS_CC  ?= x86_64-w64-mingw32-gcc
OBJDUMP   ?= x86_64-w64-mingw32-objdump
NM        ?= x86_64-w64-mingw32-nm
TARGET    := x86_64-pc-windows-gnu
PROFILE   := release

REL_DIR   := target/$(TARGET)/$(PROFILE)
RUST_LIB  := $(REL_DIR)/libmagos_discovery.a
DLL       := magos_shell.dll
LAUNCHER  := magos_launcher.exe

SHELL_SRC := shell/src/dllmain.c
MINHOOK_SRC := shell/vendor/minhook/src/buffer.c \
               shell/vendor/minhook/src/hook.c \
               shell/vendor/minhook/src/trampoline.c \
               shell/vendor/minhook/src/hde/hde64.c
INCLUDES  := -I shell/include -I shell/vendor/minhook/include

# Rust std's Windows system-lib dependencies (mingw).
RUST_SYS  := -lpsapi -lkernel32 -luser32 -lws2_32 -lbcrypt -luserenv -lntdll -lgcc

.PHONY: all build dll launcher check test clean rust-staticlib

all: build

rust-staticlib: $(RUST_LIB)
$(RUST_LIB):
	$(CARGO) build --$(PROFILE) -p magos-discovery --target $(TARGET)

build: dll launcher

dll: $(DLL)
$(DLL): $(RUST_LIB) $(SHELL_SRC) $(MINHOOK_SRC) shell/include/magos_discovery.h
	$(CROSS_CC) -shared -static-libgcc -O2 -o $@ \
	  $(SHELL_SRC) $(MINHOOK_SRC) $(INCLUDES) \
	  -l:libmagos_discovery.a -L $(REL_DIR) \
	  $(RUST_SYS) -Wl,--out-implib,magos_shell.lib

launcher: $(LAUNCHER)
$(LAUNCHER): launcher/src/launcher.c
	$(CROSS_CC) -static-libgcc -O2 -o $@ launcher/src/launcher.c -lkernel32

check: $(DLL)
	@echo "--- file type ---"; file $(DLL)
	@echo "--- entry point ---"; $(OBJDUMP) -p $(DLL) | grep -i "AddressOfEntryPoint"
	@echo "--- DllMain symbol ---"; $(NM) $(DLL) | grep -iE " T DllMain$$"
	@echo "--- Rust seam symbols ---"; $(NM) $(DLL) | grep -iE " T magos_(discover|test_panic)"
	@echo "--- runtime DLL deps (no magos_discovery.dll / libgcc) ---"; $(OBJDUMP) -p $(DLL) | grep "DLL Name"
	@test -n "$$($(NM) $(DLL) | grep -iE ' T DllMain$$')" && echo "CHECK PASS: valid PE DLL with DllMain" || (echo "CHECK FAIL"; exit 1)

test:
	$(CARGO) test -p magos-discovery

clean:
	$(CARGO) clean
	rm -f $(DLL) $(LAUNCHER) magos_shell.lib
