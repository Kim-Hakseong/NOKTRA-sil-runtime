# Sil.NativeSpec

The native side of SIL Runtime: the canonical C header for the model ABI and the reference
models written against it.

This is a **source-only** folder, not a .NET project. Nothing here is compiled by
`dotnet build`; the shared libraries are produced by a C compiler, either by a user building
their own model or by the test run compiling the references below.

```
include/sil_model.h            canonical C declaration of ABI v1
src/sil_first_order.c          reference plant   — first-order lag, RK4
src/sil_pi_controller.c        reference control code — discrete PI with anti-windup
```

The normative contract is `spec/native-abi.md` (frozen at v1). The header and these sources
follow it; if the three ever disagree, the spec wins.

## Building

```sh
cc -O2 -std=c11 -ffp-contract=off -shared -fPIC -Iinclude src/sil_first_order.c \
   -o libsil_first_order.dylib      # macOS; .so on Linux
```

`-ffp-contract=off` matters. Without it the compiler may fuse a multiply and an add into a
single FMA, which is more accurate but produces a *different* number than the managed
integrator. The tests assert that the C model and `FirstOrderLagModel` agree bit-for-bit, and
that only holds when neither side contracts.

## Writing your own model

Implement the eight entry points in `sil_model.h`. The rules that matter:

- **No global mutable state.** `sil_init` must be callable several times to produce independent
  instances.
- **Be deterministic.** No randomness, no wall-clock, no threads, no I/O. The runtime's whole
  value is that the same run produces the same answer.
- **Publish outputs before returning** from `sil_init` and from every `sil_step`.
- **Port indices are positions.** `sil_port_info(i)` must report `index == i`.

The loader (`src/Sil.Core/Native/`) checks the ABI version, resolves all eight exports and
validates the port table before the model reaches the execution cycle, so a mistake in any of
these shows up as a load error naming the library rather than as bad numbers in a result file.
