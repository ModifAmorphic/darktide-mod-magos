/*
 * sha256.c - Self-contained SHA-256 (FIPS 180-4).
 *
 * Reference public-domain implementation; no OpenSSL dependency so the
 * engine builds cleanly under both native gcc and mingw. The tool
 * wrapper hashes the file bytes with dt_sha256_hex() and stuffs the
 * resulting hex string into dt_result_t.sha256 for the output report.
 */
#include "engine.h"
#include "util.h"
#include <stdio.h>

typedef struct {
    uint32_t state[8];
    uint64_t bitlen;
    uint8_t  buf[64];
    size_t   buflen;
} dt_sha256_ctx_t;

static const uint32_t K[64] = {
    0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,
    0x923f82a4,0xab1c5ed5,0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,
    0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,0xe49b69c1,0xefbe4786,
    0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
    0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,
    0x06ca6351,0x14292967,0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,
    0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,0xa2bfe8a1,0xa81a664b,
    0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
    0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,
    0x5b9cca4f,0x682e6ff3,0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,
    0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2
};

#define ROTR(x,n) (((x) >> (n)) | ((x) << (32 - (n))))
#define EP0(x) (ROTR(x,2)  ^ ROTR(x,13) ^ ROTR(x,22))
#define EP1(x) (ROTR(x,6)  ^ ROTR(x,11) ^ ROTR(x,25))
#define SIG0(x) (ROTR(x,7) ^ ROTR(x,18) ^ ((x) >> 3))
#define SIG1(x) (ROTR(x,17) ^ ROTR(x,19) ^ ((x) >> 10))

static void sha256_block(dt_sha256_ctx_t *c, const uint8_t *p) {
    uint32_t M[64];
    for (int i = 0; i < 16; ++i)
        M[i] = (uint32_t)p[i*4] << 24 | (uint32_t)p[i*4+1] << 16 |
               (uint32_t)p[i*4+2] << 8 | (uint32_t)p[i*4+3];
    for (int i = 16; i < 64; ++i)
        M[i] = SIG1(M[i-2]) + M[i-7] + SIG0(M[i-15]) + M[i-16];

    uint32_t a=c->state[0], b=c->state[1], cc=c->state[2], d=c->state[3];
    uint32_t e=c->state[4], f=c->state[5], g=c->state[6], h=c->state[7];
    for (int i = 0; i < 64; ++i) {
        uint32_t t1 = h + EP1(e) + ((e & f) ^ ((~e) & g)) + K[i] + M[i];
        uint32_t t2 = EP0(a) + ((a & b) ^ (a & cc) ^ (b & cc));
        h=g; g=f; f=e; e=d+t1; d=cc; cc=b; b=a; a=t1+t2;
    }
    c->state[0]+=a; c->state[1]+=b; c->state[2]+=cc; c->state[3]+=d;
    c->state[4]+=e; c->state[5]+=f; c->state[6]+=g; c->state[7]+=h;
}

void dt_sha256_init(dt_sha256_ctx_t *c) {
    c->state[0]=0x6a09e667; c->state[1]=0xbb67ae85;
    c->state[2]=0x3c6ef372; c->state[3]=0xa54ff53a;
    c->state[4]=0x510e527f; c->state[5]=0x9b05688c;
    c->state[6]=0x1f83d9ab; c->state[7]=0x5be0cd19;
    c->bitlen=0; c->buflen=0;
}

void dt_sha256_update(dt_sha256_ctx_t *c, const uint8_t *data, size_t len) {
    c->bitlen += (uint64_t)len * 8;
    while (len > 0) {
        size_t take = 64 - c->buflen;
        if (take > len) take = len;
        memcpy(c->buf + c->buflen, data, take);
        c->buflen += take; data += take; len -= take;
        if (c->buflen == 64) {
            sha256_block(c, c->buf);
            c->buflen = 0;
        }
    }
}

void dt_sha256_final(dt_sha256_ctx_t *c, uint8_t out[32]) {
    /* Append 0x80, pad, then 64-bit big-endian length. */
    c->buf[c->buflen++] = 0x80;
    if (c->buflen > 56) {
        while (c->buflen < 64) c->buf[c->buflen++] = 0;
        sha256_block(c, c->buf);
        c->buflen = 0;
    }
    while (c->buflen < 56) c->buf[c->buflen++] = 0;
    uint64_t bl = c->bitlen;
    for (int i = 7; i >= 0; --i) c->buf[56 + (7 - i)] = (uint8_t)(bl >> (8 * i));
    sha256_block(c, c->buf);
    for (int i = 0; i < 8; ++i) {
        out[i*4+0] = (uint8_t)(c->state[i] >> 24);
        out[i*4+1] = (uint8_t)(c->state[i] >> 16);
        out[i*4+2] = (uint8_t)(c->state[i] >>  8);
        out[i*4+3] = (uint8_t)(c->state[i]);
    }
}

void dt_sha256_hex(const uint8_t *data, size_t len, char *out65) {
    dt_sha256_ctx_t c;
    dt_sha256_init(&c);
    dt_sha256_update(&c, data, len);
    uint8_t dig[32];
    dt_sha256_final(&c, dig);
    static const char hex[] = "0123456789abcdef";
    for (int i = 0; i < 32; ++i) {
        out65[i*2+0] = hex[dig[i] >> 4];
        out65[i*2+1] = hex[dig[i] & 0xF];
    }
    out65[64] = '\0';
}
