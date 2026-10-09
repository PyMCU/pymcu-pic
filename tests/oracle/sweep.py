"""Runs the pymcu-avr language oracle corpus (~616 probes, tests/oracle/probes/*.py) against
the PIC targets (pic18f45k50, pic16f877a) this repo builds for, using PicSharp.TestKit
(Pic18F45K50Simulation / Pic16F877ASimulation) as the reference emulator the same way
pymcu-avr uses avr8sharp and pymcu-arm-oraclesweep uses RP2040Sharp/RP2350Sharp.
MEASUREMENT ONLY: this does not modify the shared corpus (read from a detached pymcu-avr
worktree, untouched) and does not fix any compiler behaviour it finds.

Doctrine (pic-parity-matrix-2026-08, confirmed by that campaign's own auditor three times):
a probe seeded with a LITERAL constant lets the compiler's constant folder fold the whole
computation at compile time, so a broken runtime operator still prints the right answer --
COMPILES/PRINTS RIGHT != THE OPERATOR WORKS. Every seed substitution below (adaptation 2)
therefore replaces the AVR-only GPIOR0 register with a real, runtime, read/write memory
location the compiler cannot fold through -- not a literal -- the same fix the ARM sweep
already applies for the identical reason (rp2040/rp2350 have no GPIOR0 either). The matrix's
OWN counter-doctrine applies too: a probe whose point IS compile-time folding (tuples, zip,
list-comprehensions, range unrolling) must stay on its original literal seed, or the sweep
would manufacture gaps that do not exist by defeating folding the construct is supposed to
get. adapt_source only ever touches the GPIOR0/BASE import lines; a probe that never imports
them is untouched and folds exactly as written, same as it always did on AVR.

Four adaptations are made, all mechanical and applied uniformly to every probe (mirrors
pymcu-arm-oraclesweep/tests/oracle/sweep.py's adaptations 1-5 one for one; see that file's
docstring for the fuller rationale each one shares):

1. The CPython reference run's chip shim is set to the real target chip/arch instead of the
   hardcoded atmega328p/avr pymcu-avr's own test_oracle.py uses -- needed so a probe that
   branches on `__CHIP__.name`/`.arch` (e.g. 175_chip_conditional.py) is compared fairly.
2. Probes seeding a value from `pymcu.chips.atmega328p.GPIOR0` (a spare AVR register,
   `ptr(0x3E)`, boots to 0, read-only in this whole corpus) get an equivalent inline
   register at a scratch RAM address instead -- a real, zero-initialized, read/write byte,
   not a folded constant, the same ptr[uint8] shape the real chip register tables use.
   PIC16F877A: common RAM 0x70 (bank-independent by hardware design -- the same 16 bytes,
   0x70-0x7F, alias into every bank on PIC14 mid-range parts, which is exactly why every
   PIC14 toolchain treats this block as safe scratch with no bank-select bookkeeping).
   PIC18F45K50: 0x850, 16 bytes below the top of its 2048-byte GPR space (RAM_START 0x060 +
   RAM_SIZE 2048 = 0x860 exclusive top) -- far from where the compiler's own stack/variable
   allocator starts growing upward from _stack_base (0x60), mirroring the ARM sweep's own
   "near the top of SRAM" choice for the identical reason (stay clear of the allocator).
3. A handful of probes (196, 277; 278 shares the line but never runs it) declare a `BASE`
   constant that assumes the ATmega328P's low SRAM map (0x0400, or 0x04 used as a page
   number via `BASE * 256` == 0x0400) and build a `ptr()` from it. Both PIC targets' data
   spaces are far smaller than AVR's low SRAM map already, so unlike the ARM sweep (which
   had to widen BASE to uint32 -- RP2040/RP2350 SRAM addresses do not fit a uint16) no width
   change is needed here: the replacement keeps the SAME uint16/uint8 declaration, just a
   PIC-sized scratch address, placed so it never overlaps GPIOR0's replacement.
4. One probe (461) imports the WHOLE chip module (`import pymcu.chips.atmega328p as chip`)
   to exercise `getattr(module, name, default)`. The import is swapped for the equivalent
   import of the TARGET chip's own module, same alias; run_cpython_for_target shims that
   module into sys.modules too, the same shape install_cpython_shims gives atmega328p's.

Everything else about a probe -- its logic, its expected divergences, its `# tracked:` bugs
-- is measured as found.

Usage: .venv/bin/python tests/oracle/sweep.py --chip pic18f45k50 --out /path/out.jsonl
       [--limit N] [--only NAME]
"""
from __future__ import annotations

import argparse
import contextlib
import importlib.util
import io
import json
import re
import subprocess
import sys
import tempfile
import time
import types
from pathlib import Path

AVR_ORACLE_ROOT = Path.home() / "Repos" / "pymcu-avr-oraclesweep-ref-pic"
PROBES_DIR = AVR_ORACLE_ROOT / "tests" / "oracle" / "probes"
TEST_ORACLE_FILE = AVR_ORACLE_ROOT / "tests" / "oracle" / "test_oracle.py"

REPO_ROOT = Path(__file__).resolve().parents[2]  # pymcu-pic-oraclesweep worktree
RUNNER_DLL = REPO_ROOT / "build" / "oracle" / "PyMCU.OracleRunner.PIC.dll"

GPIOR_IMPORT_RE = re.compile(
    r"from pymcu\.chips\.atmega328p import ([A-Za-z0-9_, ]+)")

CHIP_MODULE_IMPORT_RE = re.compile(
    r"import pymcu\.chips\.atmega328p as (\w+)")

# Scratch byte standing in for the AVR's GPIOR0 spare register -- see module docstring,
# adaptation 2, for why each address was picked. A probe naming more than one register
# (583's GPIOR0, GPIOR1) gets one scratch byte per name, immediately above this one.
SCRATCH_ADDR = {
    "pic16f877a": 0x70,
    "pic18f45k50": 0x850,
}

# 196's `BASE: const[uint16] = 0x0400` and 277/278's `BASE: const[uint8] = 0x04` (page
# number, the probe body itself does `BASE * 256`) both assume AVR's low SRAM map, which
# does not exist on either PIC target. These sit below SCRATCH_ADDR's byte(s) with enough
# room for 196's `ptr(BASE + 256)` and 277's two-register block, so neither collides with
# GPIOR0's replacement or each other. No width change needed (see adaptation 3 above).
# pic16f877a has no entry: see NO_SAFE_PAGE_ADDRESS_PIC14.
BASE196_ADDR = {
    "pic18f45k50": 0x800,
}
# Stored pre-divided by 256: the probes' own source multiplies BASE back by 256.
BASE277_PAGE = {
    "pic18f45k50": 0x700 // 256,
}

# PIC16F877A's whole data space is 368 bytes (0x20-0x1EF across 4 banks), and its low
# addresses in EVERY bank mirror the same handful of special registers (INDF/PCL/STATUS/
# FSR at each bank's own 0x00-0x04) -- confirmed against lib/src/pymcu/chips/pic16f877a.py's
# own register table, which lists bank2's SFRs starting at 0x10C, meaning 0x100-0x10B are
# some mix of mirrored specials, not free GPR. 277/278's `BASE * 256` needs BASE*256 ITSELF
# to be a safe, real GPR byte -- the only candidates under 0x200 are 0x000 (INDF, a real
# register, not scratch) and 0x100 (a mirrored special, confirmed above) -- there is no
# third candidate before the chip runs out of address space. 196's runtime-computed `ptr(BASE
# + off)` surfaced a second, separate question this sweep cannot safely resolve either: an
# early try at BASE=0x60 (off=256, target 0x160, presumed bank-2 GPR) wrote through the
# runtime pointer and read back 0 through the equivalent CONSTANT pointer -- which could be a
# real PIC14 bug (runtime pointer arithmetic not selecting the bank a compile-time-constant
# address gets right) or could be this sweep hitting an address this script cannot confirm is
# real GPR without the datasheet's bank map in hand. Measuring a "bug" that is actually this
# script's own unsafe address choice would be worse than not measuring it, so both probe
# families (196, 277, 278) are no_medido on pic16f877a and measured cleanly on pic18f45k50
# (confirmed: 196 and 277 both match there), which has 2048 bytes of flat-enough GPR and no
# equivalent low-address mirroring to dodge.
NO_SAFE_PAGE_ADDRESS_PIC14 = (
    "pic16f877a's low memory mirrors INDF/PCL/STATUS/FSR in every bank and has no third "
    "256-aligned byte to stand in for BASE after INDF (0x000) and the mirrored specials at "
    "0x100; measured cleanly on pic18f45k50 instead (see sweep.py's own comment)."
)

TARGET_CHIP_INFO = {
    "pic16f877a": ("pic16f877a", "pic14"),
    "pic18f45k50": ("pic18f45k50", "pic18"),
}

TARGET_FREQUENCY = {
    "pic16f877a": 20_000_000,
    "pic18f45k50": 16_000_000,
}


def _diagnostic_matches(expected: str, log: str) -> bool:
    normalized_log = " ".join(log.split())
    return " ".join(expected.split()) in normalized_log


def _load_avr_oracle_module():
    spec = importlib.util.spec_from_file_location("avr_test_oracle", TEST_ORACLE_FILE)
    module = importlib.util.module_from_spec(spec)
    sys.modules["avr_test_oracle"] = module
    sys.path.insert(0, str(TEST_ORACLE_FILE.parent))
    try:
        spec.loader.exec_module(module)
    finally:
        sys.path.pop(0)
    return module


avr_oracle = _load_avr_oracle_module()


def probe_files() -> list[Path]:
    return sorted(PROBES_DIR.glob("*.py"))


def adapt_source(src: str, chip: str) -> str:
    def gpior_sub(m: re.Match) -> str:
        names = [n.strip() for n in m.group(1).split(",") if n.strip()]
        decls = "; ".join(
            f"{name}: ptr[uint8] = ptr({hex(SCRATCH_ADDR[chip] + i)})"
            for i, name in enumerate(names)
        )
        return f"from pymcu.types import ptr, uint8; {decls}"

    src = GPIOR_IMPORT_RE.sub(gpior_sub, src)

    src = CHIP_MODULE_IMPORT_RE.sub(rf"import pymcu.chips.{chip} as \1", src)

    base196 = "BASE: const[uint16] = 0x0400"
    if base196 in src:
        src = src.replace(base196, f"BASE: const[uint16] = {hex(BASE196_ADDR[chip])}")

    base277 = "BASE: const[uint8] = 0x04"
    if base277 in src:
        src = src.replace(base277, f"BASE: const[uint8] = {hex(BASE277_PAGE[chip])}")

    return src


def run_cpython_for_target(src: str, probe_name: str, chip_name: str, arch: str) -> str:
    """Same as avr_oracle.run_cpython, but the chip shim reports the real target chip
    instead of the hardcoded atmega328p/avr -- see module docstring, adaptation 1."""
    buf = io.StringIO()
    previous = avr_oracle.install_cpython_shims()
    chip_mod = sys.modules["pymcu.chips.atmega328p"]
    chip_mod.__CHIP__.name = chip_name
    chip_mod.__CHIP__.arch = arch
    sys.modules["pymcu.chips"].__CHIP__ = chip_mod.__CHIP__

    # Adaptation 4 (461): a fake pymcu.chips.<target> module, GPIOR0 shimmed the same shape
    # as atmega328p's own, so `import pymcu.chips.<chip> as chip; getattr(chip, "GPIOR0",
    # None)` resolves to the SAME kind of object the firmware build's adapted source reads
    # through its scratch ptr.
    target_chip_mod = types.ModuleType(f"pymcu.chips.{chip_name}")
    target_chip_mod.GPIOR0 = types.SimpleNamespace(value=chip_mod.GPIOR0.value)
    sys.modules[f"pymcu.chips.{chip_name}"] = target_chip_mod

    globals_ = {"__name__": "__main__", "__file__": str(PROBES_DIR / f"{probe_name}.py")}
    try:
        with contextlib.redirect_stdout(buf):
            try:
                exec(compile(src, probe_name, "exec"), globals_)
            except SystemExit:
                pass
            except BaseException as exc:  # noqa: BLE001 - mirrors avr_oracle.run_cpython
                msg = str(exc)
                print(f"E:{type(exc).__name__}: {msg}" if msg else f"E:{type(exc).__name__}")
    finally:
        avr_oracle.restore_modules(previous)
    return buf.getvalue()


def compile_probe(tmp_dir: Path, name: str, src: str, chip: str, pymcu: Path) -> tuple[int, str | None, str]:
    project = tmp_dir / name
    src_dir = project / "src"
    src_dir.mkdir(parents=True)
    (src_dir / "main.py").write_text(src)
    (project / "pyproject.toml").write_text(
        "[project]\n"
        'name = "oracle-probe"\n'
        'version = "0.1.0"\n'
        'requires-python = ">=3.11"\n\n'
        "[tool.pymcu]\n"
        f'target = "{chip}"\n'
        f'frequency = {TARGET_FREQUENCY[chip]}\n'
        'sources = "src"\n'
        'entry = "main.py"\n'
    )
    result = subprocess.run(
        [str(pymcu), "build"], cwd=project, capture_output=True, text=True, timeout=90,
    )
    hex_path = project / "dist" / "firmware.hex"
    return result.returncode, (str(hex_path) if hex_path.is_file() else None), result.stdout + result.stderr


def run_emulator(chip: str, firmware_hex: str, max_steps: int = 4_000_000) -> str:
    result = subprocess.run(
        ["dotnet", str(RUNNER_DLL), chip, firmware_hex, str(max_steps)],
        capture_output=True, text=True, timeout=120,
    )
    return result.stdout.replace("\r\n", "\n")


def evaluate(probe: Path, chip: str, tmp_dir: Path, pymcu: Path) -> dict:
    src = probe.read_text()
    expectation = avr_oracle.parse_expectation(src)
    name = probe.stem
    record = {"probe": probe.name, "expect": expectation.kind, "chip": chip}
    if expectation.tracked:
        record["tracked"] = expectation.tracked

    if expectation.frontend is not None and expectation.frontend != "default":
        record["outcome"] = "no_medido"
        record["reason"] = f"restricted to the {expectation.frontend} front end; this sweep runs the default front end only"
        return record

    # BASE196/BASE277 (adaptation 3): no_medido on pic16f877a specifically -- see
    # NO_SAFE_PAGE_ADDRESS_PIC14's own comment for why PIC14's banked, heavily
    # mirrored low memory has no address this mechanical substitution can trust.
    if chip == "pic16f877a" and (
        "BASE: const[uint16] = 0x0400" in src or "BASE: const[uint8] = 0x04" in src
    ):
        record["outcome"] = "no_medido"
        record["reason"] = NO_SAFE_PAGE_ADDRESS_PIC14
        return record

    chip_name, arch = TARGET_CHIP_INFO[chip]
    adapted = adapt_source(src, chip)
    returncode, hex_path, log = compile_probe(tmp_dir, name, adapted, chip, pymcu)

    if expectation.kind == "refuse":
        if returncode != 0:
            if expectation.diagnostic and not _diagnostic_matches(expectation.diagnostic, log):
                record["outcome"] = "refused_wrong_diagnostic"
                record["detail"] = log.strip().splitlines()[-1][:200] if log.strip() else ""
            else:
                record["outcome"] = "refused"
            return record
        record["outcome"] = "unexpected_compile_success"
        return record

    if returncode != 0 or hex_path is None:
        interesting = [
            line for line in log.splitlines()
            if any(k in line.lower() for k in ("error", "exception", "unsupported", "refus"))
        ]
        detail = (interesting or log.splitlines() or [""])[-1][:200]
        record["outcome"] = "compile_fail"
        record["detail"] = detail
        return record

    try:
        actual = run_emulator(chip, hex_path)
    except subprocess.TimeoutExpired:
        record["outcome"] = "runner_timeout"
        return record

    expected = run_cpython_for_target(src, name, chip_name, arch)
    if expectation.kind == "divergence":
        expected = avr_oracle.apply_divergence(expectation.divergence_doc, expected)

    if expected == actual:
        record["outcome"] = "match"
    elif "END\n" not in actual and actual.strip() == "":
        record["outcome"] = "crash_or_timeout"
    else:
        record["outcome"] = "mismatch"
        record["detail"] = avr_oracle.first_difference(expected, actual)
        record["expected_tail"] = expected[-200:]
        record["actual_tail"] = actual[-200:]
    return record


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--chip", required=True, choices=["pic18f45k50", "pic16f877a"])
    ap.add_argument("--out", required=True)
    ap.add_argument("--limit", type=int, default=None)
    ap.add_argument("--only", default=None, help="substring filter on probe filename")
    ap.add_argument("--pymcu", default=str(REPO_ROOT / ".venv" / "bin" / "pymcu"))
    args = ap.parse_args()

    pymcu = Path(args.pymcu)
    assert pymcu.is_file(), f"pymcu binary not found: {pymcu}"
    assert RUNNER_DLL.is_file(), f"runner not built: {RUNNER_DLL}"

    probes = probe_files()
    if args.only:
        probes = [p for p in probes if args.only in p.name]
    if args.limit:
        probes = probes[: args.limit]

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    t0 = time.time()
    with out_path.open("w") as fh:
        for i, probe in enumerate(probes, 1):
            with tempfile.TemporaryDirectory() as tmp:
                try:
                    record = evaluate(probe, args.chip, Path(tmp), pymcu)
                except Exception as exc:  # noqa: BLE001 - keep sweeping past a bad probe
                    record = {"probe": probe.name, "chip": args.chip, "outcome": "sweep_error",
                              "detail": repr(exc)}
            fh.write(json.dumps(record) + "\n")
            fh.flush()
            if i % 25 == 0 or i == len(probes):
                elapsed = time.time() - t0
                print(f"[{args.chip}] {i}/{len(probes)} ({elapsed:.0f}s)", file=sys.stderr)


if __name__ == "__main__":
    main()
