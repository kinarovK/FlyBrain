"""The sprint gate scripts must list every step in dry-run mode."""
from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
PS1 = ROOT / "scripts" / "gate.ps1"
SH = ROOT / "scripts" / "gate.sh"
STEPS = ("build", "dotnet-test", "pytest", "brain-start", "real-tests", "brain-stop")


def test_gate_ps1_declares_every_step():
    text = PS1.read_text(encoding="utf-8")
    for step in STEPS:
        assert f'Name = "{step}"' in text
    for switch in ("-DryRun", "-NoReal", "-RecordGolden", "-Sprint"):
        assert switch.lstrip("-") in text


@pytest.mark.skipif(shutil.which("pwsh") is None and shutil.which("powershell") is None, reason="PowerShell not available")
def test_gate_ps1_dry_run_lists_steps():
    shell = shutil.which("pwsh") or shutil.which("powershell")
    completed = subprocess.run(
        [shell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(PS1), "-DryRun", "-Sprint", "test"],
        capture_output=True, text=True, encoding="utf-8", errors="replace", check=False, timeout=60,
    )
    assert completed.returncode == 0, completed.stderr
    for step in STEPS:
        assert f"STEP {step}:" in completed.stdout


@pytest.mark.skipif(sys.platform == "win32" or shutil.which("bash") is None, reason="bash gate is for Linux/CI")
def test_gate_sh_dry_run_lists_fast_steps():
    completed = subprocess.run(
        [shutil.which("bash"), str(SH), "--dry-run"],
        capture_output=True, text=True, encoding="utf-8", errors="replace", check=False, timeout=60,
    )
    assert completed.returncode == 0, completed.stderr
    for step in ("build", "dotnet-test", "pytest", "real-tests"):
        assert f"STEP {step}:" in completed.stdout
