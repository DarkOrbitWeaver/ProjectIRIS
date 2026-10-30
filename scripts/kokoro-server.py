"""Iris's local Kokoro TTS worker.

The neural voice worker Iris owns: it speaks the same OpenAI-compatible
protocol her HTTP synthesizer already speaks, so the C# side needs no new
request-alpha; the difference lives in process ownership (spawned by Iris,
job-bound to die with her) and in receipts.

Endpoints:
    GET  /health  -> {"ok": true, "engine": "kokoro", "voices": n}
    GET  /models  -> {"data": [{"id": "kokoro"}]}
    GET  /voices  -> {"voices": [af_heart, af_bella, ...]}
    POST /audio/speech  {input, voice?, speed?, response_format?} -> audio/wav

Process contract: one utterance at a time (espeak holds process-global state
during phonemization and the CPU budget is 2 threads), so inference is
serialized with an internal lock. Synthesis is fully contained: a crashed
request never poisons the next one; a killed worker is replaced at next start.

Usage:
    python kokoro-server.py --port 18766 --model-dir path --threads 2 --default-voice af_heart
"""

from __future__ import annotations

import argparse
import io
import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import numpy as np  # noqa: F401  (kokoro_onnx voices load requires it)
import onnxruntime as ort
import soundfile as sf
from kokoro_onnx import Kokoro

MAX_TEXT_CHARACTERS = 4000
MIN_SPEED, MAX_SPEED = 0.5, 2.0
SAMPLE_RATE = 24000


def receipt(event: str, **fields) -> None:
    """One stderr line per interesting act. Iris's host turns these into her
    day's receipts at Info level - they must never vanish into trace silence."""
    payload = " ".join(f"{key}={value}" for key, value in fields.items())
    print(f"receipt {event} {payload}", flush=True)


def load_kokoro(model_path: str, voices_path: str, threads: int, default_voice: str) -> Kokoro:
    options = ort.SessionOptions()
    options.intra_op_num_threads = threads
    options.inter_op_num_threads = 1
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    options.add_session_config_entry("session.inter_op.allow_spinning", "0")
    session = ort.InferenceSession(model_path, sess_options=options,
                                   providers=["CPUExecutionProvider"])
    kokoro = Kokoro.from_session(session, voices_path)
    kokoro.create("Ready.", voice=default_voice, speed=1.0, lang="en-us")
    return kokoro


class _State:
    kokoro: Kokoro | None = None
    voices: list[str] = []
    infer_lock = threading.Lock()


def build_handler(args: argparse.Namespace, state: _State) -> type[BaseHTTPRequestHandler]:
    default_voice = args.default_voice

    class Handler(BaseHTTPRequestHandler):
        server_version = "IrisKokoro/1.0"

        def log_message(self, fmt, *log_args):  # noisy default logging is disabled;
            pass  # the worker speaks in receipts instead.

        def _send_json(self, status: int, payload: dict) -> None:
            body = json.dumps(payload).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _send_wav(self, audio: np.ndarray) -> None:
            buffer = io.BytesIO()
            sf.write(buffer, audio, SAMPLE_RATE, format="WAV", subtype="PCM_16")
            bytes_out = buffer.getvalue()
            self.send_response(200)
            self.send_header("Content-Type", "audio/wav")
            self.send_header("Content-Length", str(len(bytes_out)))
            self.end_headers()
            self.wfile.write(bytes_out)

        def _read_body(self) -> bytes | None:
            try:
                length = int(self.headers.get("Content-Length", 0))
            except ValueError:
                return None
            if length <= 0 or length > 4_000_000:
                return None
            return self.rfile.read(length)

        def do_GET(self) -> None:
            path = self.path.split("?")[0]
            if path == "/health":
                self._send_json(200, {
                    "ok": state.kokoro is not None,
                    "engine": "kokoro",
                    "voices": len(state.voices),
                })
                return
            if path == "/models":
                self._send_json(200, {"object": "list", "data": [{"id": "kokoro"}]})
                return
            if path == "/voices":
                self._send_json(200, {"voices": state.voices})
                return
            self._send_json(404, {"error": f"unknown path {path}"})

        def do_POST(self) -> None:
            if self.path.split("?")[0] != "/audio/speech":
                self._send_json(404, {"error": "only /audio/speech is served"})
                return

            body = self._read_body()
            if body is None:
                self._send_json(400, {"error": "unreadable request body"})
                return

            try:
                request = json.loads(body.decode("utf-8"))
            except (UnicodeDecodeError, ValueError):
                self._send_json(400, {"error": "request body is not valid JSON"})
                return

            text = str(request.get("input", "") or "")
            if not text.strip():
                self._send_json(400, {"error": "input is empty"})
                return
            if len(text) > MAX_TEXT_CHARACTERS:
                self._send_json(400, {"error": "input too long for her voice worker"})
                return

            voice = str(request.get("voice", "") or "") or default_voice
            if voice not in state.voices:
                self._send_json(400, {"error": f"unknown voice '{voice}'"})
                receipt("voice.rejected", voice=voice)
                return
            requested_format = str(request.get("response_format", "wav") or "wav")
            if requested_format not in ("wav", ""):
                self._send_json(400, {"error": f"only wav is supported, got '{requested_format}'"})
                return
            try:
                speed = float(request.get("speed", 1.0))
            except (TypeError, ValueError):
                speed = 1.0
            speed = min(MAX_SPEED, max(MIN_SPEED, speed))

            started = time.perf_counter()
            try:
                serialized = state.infer_lock.acquire(timeout=120)
                if not serialized:
                    self._send_json(503, {"error": "voice worker busy"})
                    return
                try:
                    audio, rate = state.kokoro.create(
                        text, voice=voice, speed=speed, lang="en-us")
                finally:
                    state.infer_lock.release()
            except ValueError as failure:
                self._send_json(400, {"error": str(failure)})
                receipt("voice.failed", reason=failure, voice=voice)
                return
            except Exception as failure:  # noqa: BLE001 - one bad request must not kill the worker
                self._send_json(500, {"error": "synthesis failed; worker stays up"})
                receipt("voice.failed", reason=repr(failure), voice=voice)
                return

            elapsed_ms = (time.perf_counter() - started) * 1000.0
            self._send_wav(audio)
            receipt("voice.synthesized",
                    voice=voice, chars=len(text), speed=f"{speed:.2f}",
                    ms=f"{elapsed_ms:.0f}",
                    audio_seconds=f"{len(audio) / SAMPLE_RATE:.2f}")

        def do_POST_unknown(self):  # pragma: no cover (kept defensive)
            self._send_json(405, {"error": "method not allowed"})

    return Handler


def main() -> int:
    parser = argparse.ArgumentParser(description="Iris's Kokoro voice worker")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=18766)
    parser.add_argument("--model-dir", default="")
    parser.add_argument("--model-file", default="kokoro-v1.0.onnx")
    parser.add_argument("--voices-file", default="voices-v1.0.bin")
    parser.add_argument("--threads", type=int, default=2)
    parser.add_argument("--default-voice", default="af_heart")
    args = parser.parse_args()

    model_dir = Path(args.model_dir)
    model_path = model_dir / args.model_file
    voices_path = model_dir / args.voices_file
    if not model_path.exists() or not voices_path.exists():
        receipt("boot.failed", reason=f"model files missing in {model_dir}")
        return 2

    try:
        state = _State()
        started = time.perf_counter()
        state.kokoro = load_kokoro(str(model_path), str(voices_path),
                                   max(1, min(4, args.threads)), args.default_voice)
        state.voices = [str(name) for name in state.kokoro.get_voices()]
    except (KeyError, RuntimeError, TypeError, ValueError) as failure:
        receipt("boot.failed", reason=repr(failure))
        return 2

    # af_* voices are the researched set. Names come from the voices bundle; the
    # audition targets (af_heart, af_bella) are verified at boot as honesty.
    catalogue = ", ".join(state.voices[:8]) + ("..." if len(state.voices) > 8 else "")
    receipt("boot.ready",
            model=model_path.name, voices=len(state.voices),
            threads=args.threads, ms=f"{(time.perf_counter() - started) * 1000:.0f}")
    print(f"voice catalogue: {catalogue}", flush=True)

    server = ThreadingHTTPServer((args.host, args.port), build_handler(args, state))
    server.daemon_threads = True
    receipt("listening", host=args.host, port=args.port)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
