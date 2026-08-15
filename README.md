# pymcu-pic

PIC support for [PyMCU](https://github.com/PyMCU/PyMCU): codegen backend
(PIC12 baseline, PIC14/16F mid-range, PIC18F) plus the gputils toolchain
driver (gpasm/gplink), in a single package.

## Install (users)

```bash
pip install --pre "pymcu-compiler[pic]"
```

The `[pic]` extra pulls this backend plus
[pymcu-pic-toolchain](https://github.com/PyMCU/pymcu-pic-toolchain) (pre-built
`gpasm` + device headers) — no system gputils needed.

## Layout

```
src/csharp/lib/    PyMCU.Backend.PIC — PIC12/PIC14/PIC18 codegens + PicBackendProvider
src/csharp/cli/    pymcuc-pic — AOT-compiled backend runner (.mir -> .asm)
src/python/pymcu/backend/pic/    pymcu.backends entry-point plugin (wraps pymcuc-pic)
src/python/pymcu/toolchain/pic/  pymcu.toolchains entry-point plugin (gputils)
tests/unit/        xUnit codegen tests (need the PyMCU monorepo checked out as a sibling)
tests/integration/ NUnit tests that run real firmware on the PicSharp emulator
examples/blink/    Reference example: LED on RB0, PIC16F877A @ 20 MHz
```

## Prerequisites

- .NET 10 SDK
- The `PyMCU` monorepo checked out as a sibling directory (`../PyMCU`) —
  the C# projects reference `pymcu-sdk` by relative path.
- PicSharp (not yet published) checked out as a sibling (`../PicSharp`) for
  the integration tests.
- An assembler: the [pymcu-pic-toolchain](https://github.com/PyMCU/pymcu-pic-toolchain)
  wheel (installed automatically with `pymcu-compiler[pic]`), or a system
  gputils (`brew install gputils` / `apt install gputils`).

## Build

```sh
dotnet build src/csharp/lib/PyMCU.Backend.PIC.csproj
dotnet publish src/csharp/cli/PyMCU.Backend.PIC.Cli.csproj -c Release -o build/bin --nologo
```

Install into a PyMCU dev venv (registers the `pymcu.backends`/`pymcu.toolchains`
entry points so `pymcu build` discovers the PIC backend):

```sh
uv pip install --no-deps -e .
```

## Tests

```sh
# Codegen unit tests (xUnit)
dotnet test tests/unit/PyMCU.PIC.UnitTests.csproj

# Emulator integration tests (NUnit + PicSharp.TestKit)
dotnet test tests/integration/PyMCU.IntegrationTests.csproj
```

The integration tests compile `examples/*` with the `pymcu` driver from the
sibling monorepo venv (`.venv` symlink at the repo root) and run the resulting
HEX on a simulated PIC16F877A at 20 MHz.

## Enhanced mid-range (PIC14E) — what is and is not validated

`arch = "pic14e"` selects the enhanced mid-range core (PIC16F1xxxx). It differs
from the classic mid-range in banking (`MOVLB` + BSR), context save (hardware
shadow registers) and indirect addressing (16-bit `FSR0`/`FSR1` through
`INDF0`/`INDF1`, plus `MOVIW`/`MOVWI`/`ADDFSR`, and no `STATUS<IRP>` bit). All
three live in `PIC14EStrategy` (`src/csharp/lib/Targets/PIC14/PIC14Peephole.cs`).

`Pic14EArrayOpsTests` runs array access through `FSR0`/`INDF0` on a **simulated
PIC16F1939**, and `PIC14EIndirectTests` asserts the emitted assembler. What that
leaves open:

- **The PIC16F15244 itself is not built or run anywhere.** Two things are missing
  outside this repo: gputils 1.5.2 (and the `pymcu-pic-toolchain` wheel, which
  mirrors it) ships no `p16f15244.inc`, and `pymcu-stdlib` has no
  `pymcu/chips/pic16f15244.py`. The chip module can be supplied per project —
  the compiler resolves `pymcu.chips.*` from the project's own sources first,
  which is how `Pic14EArrayOpsTests` provides its PIC16F1939 definition — but the
  assembler header cannot. **Everything about the Curiosity Nano EV09Z19A
  therefore still needs validating on real silicon.**
- **The newer families' 6-bit `MOVLB` is not emulated.** gpasm encodes `MOVLB k`
  as `0x0020|k` (5-bit BSR) for parts like the PIC16F1455/PIC16F1939 and as
  `0x0140|k` (6-bit BSR, 64 banks) for the 5-digit families — PIC16F152xx,
  PIC16F153xx, PIC16F188xx, i.e. the PIC16F15244 included. PicSharp's
  `EnhancedMidrangeDecoder` only knows the 5-bit form and mis-decodes the 6-bit
  one as `CLRW`, so firmware for those parts cannot be run in the emulator at
  all. The assembler picks the encoding from the device header, so the compiler
  needs no change for this; the emulator does. That is why the integration test
  targets a PIC16F1939 rather than the closer PIC16F18877.

## License

MIT — see [LICENSE](LICENSE).
