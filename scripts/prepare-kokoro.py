"""Download only Iris's selected Kokoro-82M ONNX voice artifacts.

Same rules as prepare-pocket-tts.py: pinned sources, pinned hashes, no tokens,
no summarizing trust in a URL - every byte that enters her voice system is
verified against a hash this script carries in plain sight.
"""

from hashlib import sha256
from urllib.request import urlopen
from pathlib import Path
import sys

BASE = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0"

# Pinned release tag model-files-v1.0, hash-verified on 2026-10-26 from the
# exact files Iris auditioned and approved.
ARTIFACTS = (
    # (filename, size-expected-sha256)
    ("kokoro-v1.0.onnx", "7d5df8ecf7d4b1878015a32686053fd0eebe2bc377234608764cc0ef3636a6c5"),
    ("voices-v1.0.bin", "bca610b8308e8d99f32e6fe4197e7ec01679264efed0cac9140fe9c29f1fbf7d"),
)

target = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("models/kokoro-cache")
target.mkdir(parents=True, exist_ok=True)

for filename, expected in ARTIFACTS:
    destination = target / filename
    if destination.exists():
        digest = sha256(destination.read_bytes()).hexdigest()
        if digest == expected:
            print(f"  verified: {filename} ({destination.stat().st_size / 1048576:.1f} MB)")
            continue
        print(f"  replacing corrupt {filename}", flush=True)

    print(f"  downloading {filename}", flush=True)
    partial = destination.with_name(destination.name + ".part")
    with urlopen(f"{BASE}/{filename}") as response, partial.open("wb") as output:
        while True:
            chunk = response.read(1 << 20)
            if not chunk:
                break
            output.write(chunk)
    digest = sha256(partial.read_bytes()).hexdigest()
    if digest != expected:
        partial.unlink(missing_ok=True)
        raise SystemExit(f"SHA-256 verification failed for {filename}: {digest}")
    partial.replace(destination)
    print(f"  verified after download: {filename}", flush=True)

print("Kokoro artifacts are ready: fp32 English v1.0 model with the 54-voice bundle.", flush=True)
