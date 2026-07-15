# pymcu-pic

PIC support for [PyMCU](https://github.com/PyMCU/pymcu): codegen backend
(PIC12 baseline, PIC14/16F mid-range, PIC18F) plus the gputils toolchain
driver (gpasm/gplink), in a single package.

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
- [PicSharp](https://github.com/begeistert/PicSharp) checked out as a sibling
  (`../PicSharp`) for the integration tests.
- gputils (`brew install gputils` / `apt install gputils`) for assembling
  (auto-downloaded and compiled on first use otherwise).

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

## License

MIT — see [LICENSE](LICENSE).
