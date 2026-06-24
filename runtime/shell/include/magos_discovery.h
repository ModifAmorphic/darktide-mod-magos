/*
 * magos_discovery.h — C-ABI of the Rust `magos-discovery` staticlib.
 *
 * The Rust crate (discovery/src/lib.rs) defines the same struct with `#[repr(C)]`;
 * this header mirrors it exactly for the C shell. All fields are RVAs (offsets
 * from the module base passed to `magos_discover`).
 */
#ifndef MAGOS_DISCOVERY_H
#define MAGOS_DISCOVERY_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint32_t lua_newstate_thunk;
    uint32_t lua_newstate_body;
    uint32_t lua_atpanic;
    uint32_t lua_gettop;
    uint32_t lual_loadbuffer;
    uint32_t lua_pcall;
    uint32_t lual_openlibs;
    uint32_t lua_pushcclosure;
    uint32_t lua_setfield;
    uint32_t lua_pushstring;
    uint32_t lua_tolstring;
    uint32_t lua_createtable;
    uint32_t lua_type;
    uint32_t lua_tonumber;
    uint32_t lua_settop;
    uint32_t lua_panic_body;
    uint32_t luaenvironment_init_begin;
    uint32_t luaenvironment_init_end;
} MagosAddressTable;

/* Return codes. */
#define MAGOS_OK              0
#define MAGOS_ERR_NULL_ARG   (-1)
#define MAGOS_ERR_PE         (-2)
#define MAGOS_ERR_DISCOVERY  (-3)
#define MAGOS_ERR_PANIC      (-100) /* a panic was caught at the boundary */

/*
 * Run discovery on an RVA-laid-out image (the live module base, or an
 * offline-mapped file). On success writes all 16 RVAs into *out and returns
 * MAGOS_OK. Panics in the pure-library are caught at the boundary (never
 * unwind into C).
 *
 *   image : pointer to the first byte of the module (GetModuleHandle(NULL)
 *           for the live game; the loader maps headers there too).
 *   len   : SizeOfImage (use GetModuleHandle + ModuleInfo.SizeOfImage).
 *   out   : writable MagosAddressTable.
 */
int magos_discover(const uint8_t *image, size_t len, MagosAddressTable *out);

/* As above, plus a NUL-terminated error string into detail[0..detail_cap). */
int magos_discover_detail(const uint8_t *image, size_t len,
                          MagosAddressTable *out,
                          uint8_t *detail, size_t detail_cap);

/* Test-only: induce a panic in the pure-library and catch it at the
 * C-ABI boundary. Returns 0 if induce==0, MAGOS_PANIC_CAUGHT if contained. */
int magos_test_panic_boundary(int induce);
#define MAGOS_PANIC_CAUGHT 0x7FFFFFB7

#ifdef __cplusplus
}
#endif

#endif /* MAGOS_DISCOVERY_H */
