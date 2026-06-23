/*
 * util.h - Small portable helpers (hex, strings, bounds).
 *
 * All inline so there is no util.c to link. Header-only, no POSIX deps.
 */
#ifndef DARKTIDE_UTIL_H
#define DARKTIDE_UTIL_H

#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>

static inline void dt_hex(char *buf, size_t bufsz, uint64_t v) {
    snprintf(buf, bufsz, "0x%llx", (unsigned long long)v);
}

static inline int dt_str_contains(const char *hay, const char *needle) {
    return needle[0] == '\0' ? 1 : (strstr(hay, needle) != NULL);
}

static inline int dt_starts_with(const char *s, const char *pfx) {
    size_t n = strlen(pfx);
    return strncmp(s, pfx, n) == 0;
}

static inline int dt_ends_with(const char *s, const char *sfx) {
    size_t ls = strlen(s), lf = strlen(sfx);
    return ls >= lf && strcmp(s + ls - lf, sfx) == 0;
}

/* Copy at most bufsz-1 chars + NUL. */
static inline void dt_strncpy(char *dst, const char *src, size_t bufsz) {
    if (bufsz == 0) return;
    size_t n = strlen(src);
    if (n >= bufsz) n = bufsz - 1;
    memcpy(dst, src, n);
    dst[n] = '\0';
}

static inline int dt_streq(const char *a, const char *b) {
    return strcmp(a, b) == 0;
}

/* Read a little-endian int32 at p. */
static inline int32_t dt_load_i32le(const uint8_t *p) {
    return (int32_t)((uint32_t)p[0] | ((uint32_t)p[1] << 8) |
                     ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24));
}

static inline uint32_t dt_load_u32le(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static inline uint64_t dt_load_u64le(const uint8_t *p) {
    return (uint64_t)dt_load_u32le(p) | ((uint64_t)dt_load_u32le(p + 4) << 32);
}

/* Parse "0x...." hex string back to a value (for the test harness). */
static inline uint64_t dt_parse_hex(const char *s) {
    while (*s == ' ' || *s == '\t') s++;
    return strtoull(s, NULL, 0);
}

#endif /* DARKTIDE_UTIL_H */
