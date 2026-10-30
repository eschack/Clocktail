// The lab's soloist (2026-10-27): the most failure-sensitive load found on an RTX 5090, from Etude / Blackwell Symphony
// (C:\Users\MING\Desktop\TR\symphony\kernels.cu, the "integer soloist" of its lanes variant), cut down to its four engines.
//
// On the reference card the SMs that fail first (16-21, then 105 and 108) fail in the integer multiply-accumulate paths:
// the INT8 tensor core (mma.sync m16n8k32 s8) first, then the 64-bit integer MAC and dp4a; never in fp16, the sfu, the
// lds or the f16 tensor paths. The soloist plays exactly those, over a quiet fp32 line, so it finds the curve's edge a
// step or two before the whole orchestra or NVIDIA's nvgwls does (Etude docs/movements.md).
//
// Every thread's work depends only on (threadIdx, seed, unit), never on blockIdx, so all blocks that play end with the
// same checksums: the host takes the majority per lane and a block that differs computed something wrong; %smid says on
// which SM, the lane on which pipe (ctrl, fp32, int = int64 MAC + dp4a, mma8 = INT8 tensor). Bit-exact, no tolerance.
//
// Divisi: a block on an SM whose bit is clear in sm_mask writes ns = 0, stays SILENT_HOLD_NS and leaves, so only the SMs
// meant to play carry the load: a narrower load draws less power, and the clock can stay on the curve's point above the
// power wall instead of being pulled under it (a pulsed load cannot: the limiter acts inside every burst).
//
// Each block also measures its own clock while it works (clock64 / globaltimer over its units), leaving out the units a
// preemption cut (the desktop takes the GPU for a millisecond, a unit lasts ~11 us): the effective clock the host compares
// with NVML's, to tell a point that really ran from one the clock never reached.
//
// Built once into solo.ptx by build_solo.py (NVRTC, compute_80: mma.sync s8 needs sm_80, nothing newer is used); the lab
// embeds the PTX and the driver compiles it for the card, as it does the lab's other kernels. No toolkit at run time.

typedef unsigned int u32;
typedef unsigned long long u64;

#define BLOCK 256
#define MIN_BLOCKS 6                 // resident blocks per SM the register allocation must allow (6 x 8 warps = 48)
#define SILENT_HOLD_NS 1000000ull    // a block on a silent SM stays this long, so the grid is placed before any slot frees
#define LANES 4
enum Lane { L_CTRL = 0, L_FP32 = 1, L_INT = 2, L_MMA8 = 3 };

__device__ __forceinline__ u32 mix32(u32 x) {
    x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16; return x;
}
// Per-thread, per-unit random words: identical in every block.
__device__ __forceinline__ u32 rnd(u32 seed, u32 tid, u32 unit, u32 k) {
    return mix32(seed ^ mix32(tid * 0x9E3779B1u + unit * 0x85EBCA6Bu + k * 0xC2B2AE35u + 0x27d4eb2fu));
}
__device__ __forceinline__ void fold32(u32& ln, u32 x) { ln = (ln ^ x) * 0x9E3779B1u; ln ^= ln >> 15; }
__device__ __forceinline__ u64 globaltimer() { u64 t; asm volatile("mov.u64 %0, %%globaltimer;" : "=l"(t)); return t; }
__device__ __forceinline__ u32 smid() { u32 s; asm volatile("mov.u32 %0, %%smid;" : "=r"(s)); return s; }
__device__ __forceinline__ float unit_float(u32 h) { return __uint_as_float(0x3F800000u | (h & 0x7FFFFFu)); }   // [1, 2)

// FP32: four 2-D rotations, 2 mul + 2 fma each, norm-preserving. Explicit .rn intrinsics, so nothing is contracted.
__device__ __forceinline__ void eng_fp32(u32 s, u32 tid, u32 unit, u32 w, u32& ln) {
    float x0 = unit_float(rnd(s, tid, unit, 0)), y0 = unit_float(rnd(s, tid, unit, 1));
    float x1 = unit_float(rnd(s, tid, unit, 2)), y1 = unit_float(rnd(s, tid, unit, 3));
    float x2 = unit_float(rnd(s, tid, unit, 4)), y2 = unit_float(rnd(s, tid, unit, 5));
    float x3 = unit_float(rnd(s, tid, unit, 6)), y3 = unit_float(rnd(s, tid, unit, 7));
    const float c = 0.96f, sn = 0.28f;
    for (u32 r = 0; r < w; r++) {
        #pragma unroll
        for (int k = 0; k < 8; k++) {
            float t0 = __fmul_rn(y0, sn), u0 = __fmul_rn(x0, sn); x0 = __fmaf_rn(x0, c, -t0); y0 = __fmaf_rn(y0, c, u0);
            float t1 = __fmul_rn(y1, sn), u1 = __fmul_rn(x1, sn); x1 = __fmaf_rn(x1, c, -t1); y1 = __fmaf_rn(y1, c, u1);
            float t2 = __fmul_rn(y2, sn), u2 = __fmul_rn(x2, sn); x2 = __fmaf_rn(x2, c, -t2); y2 = __fmaf_rn(y2, c, u2);
            float t3 = __fmul_rn(y3, sn), u3 = __fmul_rn(x3, sn); x3 = __fmaf_rn(x3, c, -t3); y3 = __fmaf_rn(y3, c, u3);
        }
    }
    fold32(ln, __float_as_uint(x0) ^ __float_as_uint(y1)); fold32(ln, __float_as_uint(x2) ^ __float_as_uint(y3));
    fold32(ln, __float_as_uint(y0) + __float_as_uint(x1)); fold32(ln, __float_as_uint(y2) + __float_as_uint(x3));
}

// Integer: 64-bit LCG (IMAD.WIDE), xorshift, mulhi, popc / brev: the INT32 half of Blackwell's shared FP32/INT32 cores.
__device__ __forceinline__ void eng_int(u32 s, u32 tid, u32 unit, u32 w, u32& ln) {
    u64 a = ((u64)rnd(s, tid, unit, 24) << 32) | rnd(s, tid, unit, 25);
    u64 b = ((u64)rnd(s, tid, unit, 26) << 32) | rnd(s, tid, unit, 27) | 1ull;
    u32 c = rnd(s, tid, unit, 28), d = rnd(s, tid, unit, 29);
    for (u32 r = 0; r < w; r++) {
        #pragma unroll
        for (int k = 0; k < 8; k++) {
            a = a * 6364136223846793005ull + 1442695040888963407ull;
            b ^= b << 13; b ^= b >> 7; b ^= b << 17;
            c = __umulhi(c, (u32)a) + __popc((u32)b) * 0x9E3779B1u;
            d = __brev(d) ^ (u32)(a >> 32) ^ (c << 5);
        }
    }
    fold32(ln, (u32)a ^ (u32)(b >> 32)); fold32(ln, c + d);
}

// DP4A: int8 dot products on the integer pipe (IDP4A).
__device__ __forceinline__ void eng_dp4a(u32 s, u32 tid, u32 unit, u32 w, u32& ln) {
    u32 a0 = rnd(s, tid, unit, 30), a1 = rnd(s, tid, unit, 31), a2 = rnd(s, tid, unit, 32), a3 = rnd(s, tid, unit, 33);
    u32 b0 = rnd(s, tid, unit, 34), b1 = rnd(s, tid, unit, 35), b2 = rnd(s, tid, unit, 36), b3 = rnd(s, tid, unit, 37);
    int c0 = 0, c1 = 0, c2 = 0, c3 = 0;
    for (u32 r = 0; r < w; r++) {
        #pragma unroll
        for (int k = 0; k < 8; k++) {
            asm volatile("dp4a.s32.s32 %0, %1, %2, %0;" : "+r"(c0) : "r"(a0), "r"(b0));
            asm volatile("dp4a.s32.s32 %0, %1, %2, %0;" : "+r"(c1) : "r"(a1), "r"(b1));
            asm volatile("dp4a.s32.s32 %0, %1, %2, %0;" : "+r"(c2) : "r"(a2), "r"(b2));
            asm volatile("dp4a.s32.s32 %0, %1, %2, %0;" : "+r"(c3) : "r"(a3), "r"(b3));
            a0 = a0 * 0x9E3779B1u + 1u; a1 = a1 * 0x85EBCA6Bu + 3u; a2 = a2 * 0xC2B2AE35u + 5u; a3 = a3 * 0x27D4EB2Fu + 7u;
        }
    }
    fold32(ln, (u32)c0 ^ (u32)c1); fold32(ln, (u32)c2 + (u32)c3);
}

// INT8 tensor core: four m16n8k32 s8 mma per round, two accumulator sets, B halves swapped (as Symphony's mma_s8).
#define MMA_S8(c0, c1, c2, c3, a0, a1, a2, a3, b0, b1) \
    asm volatile("mma.sync.aligned.m16n8k32.row.col.s32.s8.s8.s32 {%0,%1,%2,%3},{%4,%5,%6,%7},{%8,%9},{%0,%1,%2,%3};" \
        : "+r"(c0), "+r"(c1), "+r"(c2), "+r"(c3) : "r"(a0), "r"(a1), "r"(a2), "r"(a3), "r"(b0), "r"(b1))
__device__ __forceinline__ void eng_s8(u32 s, u32 tid, u32 unit, u32 w, u32& ln) {
    const u32 base = 48 + 11 * 16;     // Symphony's E_MMA_S8 = 11: the same operands as its soloist
    u32 a0 = rnd(s, tid, unit, base), a1 = rnd(s, tid, unit, base + 1), a2 = rnd(s, tid, unit, base + 2), a3 = rnd(s, tid, unit, base + 3);
    u32 a4 = rnd(s, tid, unit, base + 4), a5 = rnd(s, tid, unit, base + 5), a6 = rnd(s, tid, unit, base + 6), a7 = rnd(s, tid, unit, base + 7);
    u32 b0 = rnd(s, tid, unit, base + 8), b1 = rnd(s, tid, unit, base + 9), b2 = rnd(s, tid, unit, base + 10), b3 = rnd(s, tid, unit, base + 11);
    u32 c0 = 0, c1 = 0, c2 = 0, c3 = 0, d0 = 0, d1 = 0, d2 = 0, d3 = 0;
    for (u32 r = 0; r < w; r++) {
        MMA_S8(c0, c1, c2, c3, a0, a1, a2, a3, b0, b1); MMA_S8(d0, d1, d2, d3, a4, a5, a6, a7, b2, b3);
        MMA_S8(c0, c1, c2, c3, a0, a1, a2, a3, b2, b3); MMA_S8(d0, d1, d2, d3, a4, a5, a6, a7, b0, b1);
        fold32(ln, c0 ^ d1); fold32(ln, c2 + d3 + c1 + d0 + c3 + d2); c0 = c1 = c2 = c3 = d0 = d1 = d2 = d3 = 0;
    }
}

// One launch: `units` units of work per thread; per block LANES u64 sums (block_sum[block*LANES + lane]), the %smid it
// ran on, its busy SM cycles and nanoseconds (ns = 0: a silent SM). inject_*: one wrong bit on purpose (unit
// 0xffffffff = never), so the host can prove its judge catches one.
extern "C" __global__ void __launch_bounds__(BLOCK, MIN_BLOCKS) solo(
    u32 units, u32 seed, u32 w_fp32, u32 w_int, u32 w_dp4a, u32 w_s8, const u32* sm_mask,
    u32 inject_block, u32 inject_unit, u32 inject_lane,
    u64* block_sum, u32* block_sm, u64* block_cycles, u64* block_ns)
{
    __shared__ u32 red[BLOCK * LANES];
    const u32 tid = threadIdx.x, sm = smid();
    if (((sm_mask[(sm >> 5) & 7] >> (sm & 31)) & 1u) == 0u) {
        if (tid == 0) {
            #pragma unroll
            for (int k = 0; k < LANES; k++) block_sum[blockIdx.x * LANES + k] = 0;
            block_sm[blockIdx.x] = sm; block_cycles[blockIdx.x] = 0; block_ns[blockIdx.x] = 0;
        }
        for (const u64 t0 = globaltimer(); globaltimer() - t0 < SILENT_HOLD_NS;) __nanosleep(20000u);
        return;
    }
    // static lane indices only: a dynamically indexed lane array would live in local memory
    u32 l_ctrl = 0x5DEECE66Du ^ (tid * 0x9E3779B1u) ^ seed;
    u32 l_fp32 = l_ctrl ^ 0x85EBCA6Bu, l_int = l_ctrl ^ (2u * 0x85EBCA6Bu), l_mma8 = l_ctrl ^ (3u * 0x85EBCA6Bu);
    u64 busy_cycles = 0, busy_ns = 0, cut_cycles = 0, cut_ns = 0, unit_min_ns = ~0ull;
    for (u32 unit = 0; unit < units; unit++) {
        const u64 c0 = clock64(), t0 = globaltimer();
        fold32(l_ctrl, rnd(seed, tid, unit, 255));
        if (w_fp32) eng_fp32(seed, tid, unit, w_fp32, l_fp32);
        if (w_int) eng_int(seed, tid, unit, w_int, l_int);
        if (w_dp4a) eng_dp4a(seed, tid, unit, w_dp4a, l_int);
        if (w_s8) eng_s8(seed, tid, unit, w_s8, l_mma8);
        if (blockIdx.x == inject_block && unit == inject_unit && tid == 0) {
            if (inject_lane == L_CTRL) l_ctrl ^= 8u; else if (inject_lane == L_FP32) l_fp32 ^= 8u;
            else if (inject_lane == L_INT) l_int ^= 8u; else l_mma8 ^= 8u;
        }
        // a unit over twice the block's fastest was cut by a preemption: its time is not the SMs' clock at work
        const u64 dc = clock64() - c0, dt = globaltimer() - t0;
        if (dt != 0 && dt < unit_min_ns) unit_min_ns = dt;
        if ((dt >> 1) <= unit_min_ns) { busy_cycles += dc; busy_ns += dt; } else { cut_cycles += dc; cut_ns += dt; }
    }
    if (busy_ns < cut_ns) { busy_cycles += cut_cycles; busy_ns += cut_ns; }   // mostly cut: keep everything
    red[tid * LANES + L_CTRL] = l_ctrl; red[tid * LANES + L_FP32] = l_fp32;
    red[tid * LANES + L_INT] = l_int; red[tid * LANES + L_MMA8] = l_mma8;
    __syncthreads();
    if (tid < LANES) {
        u64 sum = 0;
        for (int i = 0; i < BLOCK; i++) sum += red[i * LANES + tid];
        block_sum[blockIdx.x * LANES + tid] = sum;
    }
    if (tid == 0) {
        block_sm[blockIdx.x] = sm; block_cycles[blockIdx.x] = busy_cycles;
        block_ns[blockIdx.x] = busy_ns ? busy_ns : 1ull;   // 0 marks a silent SM
    }
}
