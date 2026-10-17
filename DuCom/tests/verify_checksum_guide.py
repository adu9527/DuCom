"""Execute the Python examples embedded in the shipped integration guide."""
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

guide = Path(__file__).resolve().parents[2] / "Doc/BesFirmwareIntegration.zh-CN.md"
blocks = re.findall(r"```python\n(.*?)\n```", guide.read_text(encoding="utf-8"), re.S)
assert len(blocks) == 3
with tempfile.TemporaryDirectory(prefix="ducom-guide-") as temporary:
    root = Path(temporary)
    tools = root / "tools/fw_identity"
    tools.mkdir(parents=True)
    for name, source in zip(["prepare.py", "check_layout.py", "verify_image.py"], blocks):
        compile(source, name, "exec")
        (tools / name).write_text(source, encoding="utf-8")
    (root / "apps").mkdir()
    (root / "apps/main.c").write_text("/* test input */", encoding="utf-8")
    (tools / "toolchain.txt").write_text("synthetic toolchain", encoding="utf-8")
    policy = {"target": "synthetic", "bluetooth_name": "测试耳机", "inputs": ["apps", "tools"],
              "exclude": ["generated", "__pycache__"], "effective_options": ["T=synthetic"],
              "toolchain": "tools/fw_identity/toolchain.txt"}
    (tools / "policy.json").write_text(json.dumps(policy), encoding="utf-8")
    command = [sys.executable, str(tools / "prepare.py"), "--root", str(root), "--policy", str(tools / "policy.json")]
    subprocess.run(command, check=True)
    original = (root / "generated/fw_identity.h").read_bytes()
    subprocess.run(command, check=True)
    assert original == (root / "generated/fw_identity.h").read_bytes()
    subprocess.run(command + ["--verify"], check=True)
    report = json.loads((root / "generated/fw_identity.json").read_text())
    image = root / "synthetic.bin"
    image.write_bytes(("\nCHIP=best1502p\nKERNEL=RTX5\nBUILD_DATE=Oct 17 2026 12:00:00\nBUILD_SHA256="
                       + report["build_sha256"] + "\nFIRMWARE_CODE=" + report["firmware_code"]
                       + "\nCRC32_OF_IMAGE=0x00000000\n\0").encode())
    subprocess.run([sys.executable, str(tools / "check_layout.py"), str(image)], check=True)
    subprocess.run([sys.executable, str(tools / "verify_image.py"), str(image), str(root / "generated/fw_identity.json")], check=True)
    (root / "apps/main.c").write_text("/* changed input */", encoding="utf-8")
    assert subprocess.run(command + ["--verify"]).returncode != 0
    subprocess.run(command, check=True)
    assert original != (root / "generated/fw_identity.h").read_bytes()
print("PASS: embedded Python examples compile; deterministic output, layout, artifact identity and changed-input rejection verified")
