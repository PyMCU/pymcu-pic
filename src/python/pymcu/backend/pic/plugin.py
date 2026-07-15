# -----------------------------------------------------------------------------
# PyMCU PIC Backend Plugin
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------

"""
PicBackendPlugin -- PyMCU PIC codegen backend (PIC12, PIC14/16F, PIC18F).

Wraps the ``pymcuc-pic`` AOT-compiled binary that is bundled inside this
wheel.  The binary reads a ``.mir`` IR file produced by ``pymcuc --emit-ir``
and emits a PIC assembler (``.asm``) file.

Entry-point registration (pyproject.toml):
    [project.entry-points."pymcu.backends"]
    pic = "pymcu.backend.pic:PicBackendPlugin"
"""

import sys
from pathlib import Path

from pymcu.backend.sdk import BackendPlugin, LicenseStatus


class PicBackendPlugin(BackendPlugin):
    family = "pic"
    description = "PIC codegen backend (PIC12, PIC14/16F, PIC18F families)"
    version = "0.1.0a1"
    supported_arches = [
        "pic12", "baseline", "pic10f", "pic12f",
        "pic14", "pic14e", "midrange", "pic16f",
        "pic18", "advanced", "pic18f",
    ]

    @classmethod
    def get_backend_binary(cls) -> Path:
        """
        Return the path to the bundled ``pymcuc-pic`` binary.

        Search order:
        1. Adjacent to this module (wheel layout).
        2. repo_root/build/bin/ (dev: dotnet publish output).
        3. src/csharp/cli/bin/Debug/net10.0/ (dev: build shortcut).
        4. System PATH.
        """
        package_dir = Path(__file__).parent
        binary_name = "pymcuc-pic.exe" if sys.platform == "win32" else "pymcuc-pic"

        # 1. Wheel layout: binary sits next to the Python module.
        adjacent = package_dir / binary_name
        if adjacent.exists():
            cls._ensure_signed(adjacent)
            return adjacent

        # 2. Development fallback: dotnet publish output.
        # package_dir = .../pymcu-pic/src/python/pymcu/backend/pic
        # repo_root   = package_dir.parents[4]  (5 levels up)
        repo_root = package_dir.parents[4]
        dev_path = repo_root / "build" / "bin" / binary_name
        if dev_path.exists():
            cls._ensure_signed(dev_path)
            return dev_path

        # 3. CLI debug build (dev shortcut).
        runner_debug = (
            repo_root / "src" / "csharp" / "cli"
            / "bin" / "Debug" / "net10.0" / binary_name
        )
        if runner_debug.exists():
            cls._ensure_signed(runner_debug)
            return runner_debug

        # 4. System PATH.
        import shutil
        which_result = shutil.which("pymcuc-pic")
        if which_result:
            return Path(which_result)

        # Return the expected path (caller will get FileNotFoundError on invoke).
        result = package_dir / binary_name
        cls._ensure_signed(result)
        return result

    @classmethod
    def _ensure_signed(cls, binary: Path) -> None:
        """Ad-hoc sign the binary on macOS (no-op on other platforms or if already signed).
        Native AOT .NET binaries are unsigned by default; macOS kills unsigned executables.

        This runs on every backend resolution, including under parallel builds (the
        test suite spawns many ``pymcu build`` processes at once). A bare
        ``codesign --force`` rewrites the Mach-O in place, so two processes signing the
        same binary concurrently race and corrupt its header (a flipped magic byte ->
        ``Exec format error`` on the next exec). To stay safe we (1) verify first and
        skip when already validly signed, and (2) serialize the rare signing step with
        an exclusive lock so only one process ever writes the binary at a time."""
        if sys.platform != "darwin" or not binary.exists():
            return
        import subprocess

        def _is_signed() -> bool:
            try:
                return subprocess.run(
                    ["codesign", "--verify", "--strict", str(binary)],
                    check=False, capture_output=True,
                ).returncode == 0
            except FileNotFoundError:
                return True  # no codesign tool -> nothing we can (or need to) do

        if _is_signed():
            return

        import fcntl
        lock_path = Path(str(binary) + ".signlock")
        try:
            with open(lock_path, "w") as lock:
                fcntl.flock(lock, fcntl.LOCK_EX)
                # Re-check under the lock: another process may have signed it while we waited.
                if _is_signed():
                    return
                subprocess.run(
                    ["codesign", "-s", "-", "--force", str(binary)],
                    check=False, capture_output=True,
                )
        except FileNotFoundError:
            pass

    @classmethod
    def validate_license(cls, key: str | None = None) -> LicenseStatus:
        return LicenseStatus.VALID
