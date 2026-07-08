#!/usr/bin/env bash
#
# SofaBuffers C# — machine-independent instruction cost.
#
# Reports instructions retired per operation (Ir/op) under Callgrind. Unlike
# wall-clock or cycle counts, instruction counts are deterministic and
# independent of the host's clock speed and scheduler, so the numbers compare
# across machines (and against the C/C++/Rust/Go/Python/TypeScript tools — the
# workloads, ids and values are identical).
#
# The .NET runtime JITs the hot code at runtime, so there is no stable native
# symbol Callgrind could `--toggle-collect` on (a single-shot toggle also mixes
# in one-time JIT-compilation cost). So — like the Python, TypeScript and Java
# ports — each workload is run at two rep counts (R1, R2) and the whole-process
# instruction counts are subtracted:
#
#     Ir/op = ( Ir(R2) - Ir(R1) ) / ( R2 - R1 )
#
# which cancels *all* fixed cost exactly — CLR startup, JIT compilation and the
# one-time setup — leaving the pure per-op cost. For the subtraction to be clean
# the two runs must differ *only* in the measured rep count, so the runtime is
# pinned so nothing else varies between runs:
#
#   DOTNET_TieredCompilation=0   one JIT tier, compiled on first call, so the
#                                measured ops run at steady cost (no tier-up).
#   DOTNET_GCgen0size large       a gen0 big enough that the bounded run never
#                                triggers a GC, so GC adds no variable cost.
#   DOTNET_GCHeapHardLimit        caps the GC's address-space reservation, which
#                                CoreCLR otherwise sizes to physical RAM and
#                                which fails to initialize under Valgrind.
#
# Prereqs: valgrind and the .NET SDK.
# Usage:   bash bench/run_callgrind.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# Per-workload measured rep counts (R1 R2): cheap ops need a large delta so the
# residual startup jitter is negligible; the 1000-element array ops carry a huge
# per-op signal already, so a small delta keeps them fast.
REPS_CHEAP="${REPS_CHEAP:-10000 110000}"
REPS_ARRAY="${REPS_ARRAY:-200 1200}"
reps_for() {
    case "$1" in
        encode_u64_array|decode_u64_array) echo "$REPS_ARRAY";;
        *)                                 echo "$REPS_CHEAP";;
    esac
}

if ! command -v valgrind >/dev/null 2>&1; then
    echo "error: valgrind not found (needed for instruction counts)." >&2
    echo "       install it, e.g.  apt-get install valgrind" >&2
    exit 1
fi

echo ">> building (dotnet build -c Release) ..." >&2
dotnet build -c Release "$ROOT/bench/SofaBuffers.Bench" >/dev/null
DLL="$(find "$ROOT/bench" -name SofaBuffers.Bench.dll -path '*Release*' | head -1)"
if [ -z "${DLL:-}" ] || [ ! -f "$DLL" ]; then
    echo "error: could not locate the built SofaBuffers.Bench.dll." >&2
    exit 1
fi

# Runtime pinning for a deterministic, subtractable instruction count under
# Valgrind (see the header).
export DOTNET_gcServer=0
export DOTNET_TieredCompilation=0
export DOTNET_GCHeapHardLimit=0x40000000   # 1 GiB reservation cap
export DOTNET_GCgen0size=0x20000000        # 512 MiB gen0 → no GC in a bounded run

OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT
WORKLOADS=(encode_u64_array encode_typical decode_u64_array decode_typical)

run_cg() { # $1 workload, $2 reps, $3 tag
    valgrind --quiet --tool=callgrind --callgrind-out-file="$OUT/$3.out" \
        dotnet "$DLL" "$1" "$2" \
        >/dev/null 2>"$OUT/$3.log"
}

ir_of()    { grep -m1 '^summary:' "$OUT/$1.out" 2>/dev/null | awk '{print $2}'; }
bytes_of() { grep -ohE 'bytes=[0-9]+' "$OUT/$1.log" 2>/dev/null | head -1 | cut -d= -f2; }

label() {
    case "$1" in
        encode_u64_array) echo "encode: u64 array (1000)";;
        encode_typical)   echo "encode: typical message";;
        decode_u64_array) echo "decode: u64 array (1000)";;
        decode_typical)   echo "decode: typical message";;
    esac
}

echo ">> Measuring instructions/op under Callgrind (two rep counts per workload; this is slow) ..." >&2
echo
echo "==============================================================================="
echo " SofaBuffers C# instruction cost   (Callgrind, Ir/op)"
echo " instructions/op: lower is better. Deterministic & machine-independent."
echo "==============================================================================="
printf "%-26s %16s %9s\n" "Workload" "instr/op" "bytes"
printf "%-26s %16s %9s\n" "--------" "--------" "-----"

for w in "${WORKLOADS[@]}"; do
    read -r r1 r2 <<<"$(reps_for "$w")"
    run_cg "$w" "$r1" "$w.lo"
    run_cg "$w" "$r2" "$w.hi"
    lo="$(ir_of "$w.lo")"; hi="$(ir_of "$w.hi")"
    b="$(bytes_of "$w.hi")"
    iperop="$(awk -v lo="${lo:-0}" -v hi="${hi:-0}" -v ops="$(( r2 - r1 ))" \
        'BEGIN{ if (ops>0) printf "%d", (hi-lo)/ops; else print "-" }')"
    printf "%-26s %16s %9s\n" "$(label "$w")" "${iperop:--}" "${b:--}"
done
echo
echo "Ir = instructions retired (Callgrind). Independent of CPU clock and OS"
echo "scheduling; depends only on the executed code, so it compares across machines."
