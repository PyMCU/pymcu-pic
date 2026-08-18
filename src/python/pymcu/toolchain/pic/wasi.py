# -----------------------------------------------------------------------------
# PyMCU PIC Toolchain -- WASI backend
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

"""Runs gpasm as a wasm32-wasip1 module under wasmtime, with no native gputils
binary on the machine.

The module and the device headers come from the pymcu-pic-toolchain-wasi wheel,
which is architecture independent: one artifact covers every platform PyMCU
supports. The emitted .hex is byte-identical to the native gputils 1.5.2 output.

No host path ever reaches the module: the build directory is preopened as
/work and the header directory as /inc, so drive letters and backslashes never
enter WASI's path resolution.
"""

from __future__ import annotations

import platform
import tempfile
import time
from pathlib import Path
from typing import Optional


class WasiUnavailable(RuntimeError):
    """The WASI backend cannot be used (no wheel, or no wasmtime)."""


def _cache_dir() -> Path:
    """Compiled modules are tied to the OS, the CPU and the wasmtime build, so
    the cache key carries all three and lives in the user cache, never in the
    (py3-none-any) wheel."""
    from importlib.metadata import version  # noqa: PLC0415

    key = (
        f"{platform.system().lower()}-{platform.machine()}-"
        f"wasmtime{version('wasmtime')}"
    )
    path = Path.home() / ".pymcu" / "wasi-cache" / key
    path.mkdir(parents=True, exist_ok=True)
    return path


class WasiPicTools:
    """Loads gpasm once per process and re-instantiates it per invocation.

    A fresh Store and instance per run is required: gpasm is a single-shot CLI
    program that calls proc_exit, so its linear memory cannot be reused.
    Compiling the module is the expensive part and is done once, then persisted
    with Module.serialize.
    """

    def __init__(self) -> None:
        try:
            from wasmtime import Engine, Linker, Module  # noqa: PLC0415
            import pymcu_pic_toolchain_wasi as whl  # noqa: PLC0415
        except ImportError as exc:
            raise WasiUnavailable(str(exc)) from exc

        self._wasmtime = __import__("wasmtime")
        self.engine = Engine()
        self.linker = Linker(self.engine)
        self.linker.define_wasi()
        self.header_dir = whl.header_dir()

        path = whl.wasm_module("gpasm")
        cached = _cache_dir() / "gpasm.cwasm"
        if cached.exists() and cached.stat().st_mtime >= path.stat().st_mtime:
            self.module = Module.deserialize_file(self.engine, str(cached))
        else:
            self.module = Module.from_file(self.engine, str(path))
            cached.write_bytes(self.module.serialize())

    def run(self, argv: list[str], workdir: Path) -> tuple[int, str]:
        wt = self._wasmtime
        # The capture files live outside the preopened directory so they can
        # never be picked up as assembler inputs, and are read only after the
        # Store is dropped: on Windows a file cannot be deleted while a handle
        # is still open.
        with tempfile.TemporaryDirectory(prefix="pymcu-wasi-pic-") as tmp:
            out = Path(tmp) / "stdout"
            err = Path(tmp) / "stderr"

            cfg = wt.WasiConfig()
            cfg.argv = ["gpasm", *argv]
            cfg.preopen_dir(str(workdir), "/work")
            cfg.preopen_dir(str(self.header_dir), "/inc")
            cfg.stdout_file = str(out)
            cfg.stderr_file = str(err)

            store = wt.Store(self.engine)
            store.set_wasi(cfg)

            code = 0
            try:
                instance = self.linker.instantiate(store, self.module)
                instance.exports(store)["_start"](store)
            except wt.ExitTrap as exc:
                code = exc.code
            del store

            def read(p: Path) -> str:
                # Read in binary and decode UTF-8 explicitly: read_text() would
                # use the console code page on Windows.
                if not p.exists():
                    return ""
                return p.read_bytes().decode("utf-8", errors="replace")

            return code, (read(err) or read(out))


class WasiPicPipeline:
    """assemble(), the same entry point GputilsToolchain exposes."""

    def __init__(self, tools: WasiPicTools) -> None:
        self.tools = tools

    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        asm_file = Path(asm_file).resolve()
        workdir = asm_file.parent
        code, diagnostics = self.tools.run(
            [f"/work/{asm_file.name}", "-I", "/work", "-I", "/inc"], workdir
        )
        # Kept even on success: a silenced Warning[202] is how a MOVLB radix bug
        # reached silicon, where every ANSELx write was a no-op.
        self.last_diagnostics = diagnostics.replace("/work/", str(workdir) + "/")
        if code != 0:
            # The host never sees /work; report the path the user knows.
            raise RuntimeError(
                "Assembly failed:\n"
                + diagnostics.replace("/work/", str(workdir) + "/")
            )
        return asm_file.with_suffix(".hex")


_PIPELINE: Optional[WasiPicPipeline] = None
_TRIED = False


def pipeline() -> Optional[WasiPicPipeline]:
    """The shared pipeline, or None if the WASI backend is not installed."""
    global _PIPELINE, _TRIED
    if _TRIED:
        return _PIPELINE
    _TRIED = True
    try:
        _PIPELINE = WasiPicPipeline(WasiPicTools())
    except WasiUnavailable:
        _PIPELINE = None
    return _PIPELINE
