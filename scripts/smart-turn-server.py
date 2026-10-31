"""CPU-only HTTP wrapper for the open Smart Turn v3.2 ONNX model."""

from __future__ import annotations

import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import numpy as np
import onnxruntime as ort
from transformers import WhisperFeatureExtractor

SAMPLE_RATE = 16000
WINDOW_SAMPLES = 8 * SAMPLE_RATE


class TurnModel:
    def __init__(self, model_path: Path) -> None:
        options = ort.SessionOptions()
        options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        options.intra_op_num_threads = 2
        options.inter_op_num_threads = 1
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        self.session = ort.InferenceSession(
            str(model_path),
            sess_options=options,
            providers=["CPUExecutionProvider"],
        )
        self.feature_extractor = WhisperFeatureExtractor(chunk_length=8)

    def predict(self, samples: np.ndarray) -> float:
        samples = samples[-WINDOW_SAMPLES:]
        if samples.size < WINDOW_SAMPLES:
            samples = np.pad(samples, (WINDOW_SAMPLES - samples.size, 0))
        features = self.feature_extractor(
            samples,
            sampling_rate=SAMPLE_RATE,
            return_tensors="np",
            padding="max_length",
            max_length=WINDOW_SAMPLES,
            truncation=True,
            do_normalize=True,
        ).input_features.astype(np.float32, copy=False)
        return float(self.session.run(None, {"input_features": features})[0][0].item())


def build_handler(model: TurnModel) -> type[BaseHTTPRequestHandler]:
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format_string: str, *args: object) -> None:
            return

        def _send_json(self, status: int, payload: dict[str, object]) -> None:
            body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self) -> None:
            if self.path == "/health":
                self._send_json(200, {"ok": True, "model": "smart-turn-v3.2-cpu"})
            else:
                self._send_json(404, {"error": "not_found"})

        def do_POST(self) -> None:
            if self.path != "/predict":
                self._send_json(404, {"error": "not_found"})
                return

            try:
                length = int(self.headers.get("Content-Length", "0"))
            except ValueError:
                self._send_json(400, {"error": "invalid_content_length"})
                return

            if length <= 0 or length % 4 != 0 or length > WINDOW_SAMPLES * 4 * 30:
                self._send_json(400, {"error": "invalid_audio_size"})
                return

            try:
                body = self.rfile.read(length)
                if len(body) != length:
                    self._send_json(400, {"error": "incomplete_audio"})
                    return
                audio = np.frombuffer(body, dtype="<f4")
                if audio.size == 0 or not np.isfinite(audio).all():
                    self._send_json(400, {"error": "invalid_audio"})
                    return
                probability = model.predict(audio)
                self._send_json(200, {"probability": probability})
            except (ValueError, RuntimeError) as error:
                self._send_json(500, {"error": str(error)})

    return Handler


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True, type=Path)
    parser.add_argument("--port", type=int, default=18767)
    args = parser.parse_args()
    model = TurnModel(args.model)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), build_handler(model))
    print(f"ready model=smart-turn-v3.2-cpu port={args.port}", flush=True)
    try:
        server.serve_forever()
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
