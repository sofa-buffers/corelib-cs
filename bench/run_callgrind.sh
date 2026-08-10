#!/usr/bin/env bash
#
# SofaBuffers C# — machine-independent instruction cost.
#
# Reports instructions retired per operation (Ir/op) under Callgrind. Unlike
# wall-clock or cycle counts, instruction counts are deterministic and
# independent of the host's clock speed and scheduler, so the numbers compare
# across machines (and against the C/C++/Rust/Go/Python/TypeScript/Java tools —
# the workloads, ids and values are identical, because every port builds them
# from BENCH_SPEC's literals).
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
#                                measured ops run at steady cost (no tier-up);
#                                Callgrind.cs's fixed warmup — independent of the
#                                rep count, so it cancels too — puts every
#                                measured op in that compiled code.
#   DOTNET_GCgen0size large      a gen0 big enough that the bounded run never
#                                triggers a GC, so GC adds no variable cost.
#   DOTNET_GCHeapHardLimit       caps the GC's address-space reservation, which
#                                CoreCLR otherwise sizes to physical RAM and
#                                which fails to initialize under Valgrind.
#
# The two `blob 1MB` encode rows are read against each other: their difference is
# what the divisible-run flush path (CORELIB_PLAN §5.1) costs, with the host's
# memory subsystem and scheduler taken out of it. `encode: blob 1MB passthrough`
# is BENCH_SPEC's one optional row and is absent: this port implements no
# pass-through, so the row is omitted rather than filled with a placeholder.
#
# Prereqs: valgrind and the .NET SDK.
# Usage:   bash bench/run_callgrind.sh
#          WORKLOADS="encode_composite decode_composite" bash bench/run_callgrind.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# Per-workload measured rep counts (R1 R2): cheap ops need a large delta so the
# residual startup jitter is negligible; ops that carry a big per-op signal
# already need only a small one.
#
# The two blob *encode* rows take BENCH_SPEC's own advice (R1=1, R2=3): a
# megabyte of copying per op is slow under Callgrind, and the subtraction cancels
# fixed cost just as well at three reps as at three hundred. `decode: blob 1MB`
# is deliberately *not* in that class — a decode hands the visitor a window into
# the input and copies nothing, so its per-op cost is a walk over 245 chunks, and
# a delta of two ops would sit inside the run-to-run startup jitter.
REPS_CHEAP="${REPS_CHEAP:-10000 110000}"
REPS_ARRAY="${REPS_ARRAY:-200 1200}"
REPS_BLOB="${REPS_BLOB:-1 3}"
reps_for() {
    case "$1" in
        encode_blob_oneshot|encode_blob_streaming)        echo "$REPS_BLOB";;
        *_u64_array|*_composite|*_composite_skip|decode_blob) echo "$REPS_ARRAY";;
        *)                                                echo "$REPS_CHEAP";;
    esac
}

if ! command -v valgrind >/dev/null 2>&1; then
    echo "error: valgrind not found (needed for instruction counts)." >&2
    echo "       install it, e.g.  apt-get install valgrind" >&2
    exit 1
fi

echo ">> building (dotnet build -c Release) ..." >&2
dotnet build -c Release "$ROOT/bench/SofaBuffers.Bench" >/dev/null
# `*/bin/Release/*` and not just `*Release*`: obj/ holds intermediate copies and a
# reference assembly of the same name, and running one of those would measure the
# wrong thing or fail outright for want of method bodies. The project
# multi-targets, so the framework is named too rather than left to `find`'s
# directory order — an Ir/op table should not silently change runtime.
TFM="${SOFAB_TFM:-net10.0}"
DLL="$(find "$ROOT/bench" -name SofaBuffers.Bench.dll -path "*/bin/Release/$TFM/*" | head -1)"
if [ -z "${DLL:-}" ] || [ ! -f "$DLL" ]; then
    echo "error: could not locate the built $TFM SofaBuffers.Bench.dll." >&2
    exit 1
fi
echo ">> measuring $TFM ($DLL)" >&2

# Runtime pinning for a deterministic, subtractable instruction count under
# Valgrind (see the header).
export DOTNET_gcServer=0
export DOTNET_TieredCompilation=0
export DOTNET_GCHeapHardLimit=0x40000000   # 1 GiB reservation cap
export DOTNET_GCgen0size=0x20000000        # 512 MiB gen0 → no GC in a bounded run

OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT
# Order follows BENCH_SPEC's table.
WORKLOADS="${WORKLOADS:-encode_u64_array encode_typical encode_blob_oneshot \
encode_blob_streaming encode_composite decode_u64_array decode_typical \
decode_blob decode_composite decode_composite_skip}"

run_cg() { # $1 workload, $2 reps, $3 tag
    valgrind --quiet --tool=callgrind --callgrind-out-file="$OUT/$3.out" \
        dotnet "$DLL" "$1" "$2" \
        >/dev/null 2>"$OUT/$3.log"
}

ir_of()    { grep -m1 '^summary:' "$OUT/$1.out" 2>/dev/null | awk '{print $2}'; }
bytes_of() { grep -ohE 'bytes=[0-9]+' "$OUT/$1.log" 2>/dev/null | head -1 | cut -d= -f2; }

label() {
    case "$1" in
        encode_u64_array)      echo "encode: u64 array (1000)";;
        encode_typical)        echo "encode: typical message";;
        encode_blob_oneshot)   echo "encode: blob 1MB one-shot";;
        encode_blob_streaming) echo "encode: blob 1MB streaming";;
        encode_composite)      echo "encode: composite";;
        decode_u64_array)      echo "decode: u64 array (1000)";;
        decode_typical)        echo "decode: typical message";;
        decode_blob)           echo "decode: blob 1MB";;
        decode_composite)      echo "decode: composite";;
        decode_composite_skip) echo "decode: composite skip-all";;
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

for w in $WORKLOADS; do
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
echo "The blob 1MB rows are read against each other: their gap is the flush path."
