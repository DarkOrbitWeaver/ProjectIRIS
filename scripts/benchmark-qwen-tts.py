"""One-shot, GPU-only suitability check for a possible Iris voice engine.

This is intentionally not a service and it never changes Iris settings.  It downloads the
requested public model into the supplied cache, generates two short samples, writes one WAV
for listening, prints timings/VRAM, and exits so the GPU is returned to Windows.
"""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import soundfile as sf
import torch
from qwen_tts import Qwen3TTSModel


def gpu_snapshot() -> dict[str, float | int | str]:
    free, total = torch.cuda.mem_get_info(0)
    return {
        "device": torch.cuda.get_device_name(0),
        "free_mib": round(free / 1024 / 1024, 1),
        "total_mib": round(total / 1024 / 1024, 1),
        "torch_allocated_mib": round(torch.cuda.memory_allocated(0) / 1024 / 1024, 1),
        "torch_reserved_mib": round(torch.cuda.memory_reserved(0) / 1024 / 1024, 1),
    }


def synchronize() -> None:
    torch.cuda.synchronize(0)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice")
    parser.add_argument("--cache-dir", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--speaker", default="Vivian")
    args = parser.parse_args()

    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is unavailable; do not benchmark this engine on the CPU.")

    args.cache_dir.mkdir(parents=True, exist_ok=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report: dict[str, object] = {
        "model": args.model,
        "before_load": gpu_snapshot(),
    }

    started = time.perf_counter()
    model = Qwen3TTSModel.from_pretrained(
        args.model,
        device_map="cuda:0",
        dtype=torch.float16,
        cache_dir=str(args.cache_dir),
    )
    synchronize()
    report["load_seconds"] = round(time.perf_counter() - started, 3)
    report["after_load"] = gpu_snapshot()
    report["speakers"] = model.get_supported_speakers()
    report["languages"] = model.get_supported_languages()

    samples: list[tuple[str, str]] = [
        ("warmup", "Hi. I am Iris, and I am ready when you are."),
        ("short_reply", "I found it. Adding it to your playlist now."),
    ]
    for name, text in samples:
        started = time.perf_counter()
        wavs, sample_rate = model.generate_custom_voice(
            text=text,
            language="English",
            speaker=args.speaker,
        )
        synchronize()
        seconds = time.perf_counter() - started
        duration = len(wavs[0]) / sample_rate
        report[name] = {
            "generation_seconds": round(seconds, 3),
            "audio_seconds": round(duration, 3),
            "realtime_factor": round(seconds / duration, 3) if duration else None,
            "after_generation": gpu_snapshot(),
        }
        if name == "short_reply":
            sf.write(args.output, wavs[0], sample_rate)

    report["sample_wav"] = str(args.output)
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
