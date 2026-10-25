"""Download only Iris's selected public, no-cloning Pocket TTS model and profiles."""

from huggingface_hub import hf_hub_download


MODEL_REPO = "kyutai/pocket-tts-without-voice-cloning"
LANGUAGE = "english_18m_2026-10"
VOICES = ("alba", "anna", "eve", "mary", "vera", "estelle")
MODEL_FILES = (
    ("a7056f505a4a8ca72d8b5fb1440071d6f1a35739", f"languages/{LANGUAGE}/model.safetensors"),
    ("08ffc58775e62aad9409511b9591c9af626270f3", f"languages/{LANGUAGE}/tokenizer.model"),
)

VOICE_REVISION = "a3055dcb533cf2ec90458008a01e7cef550bae2a"
files = MODEL_FILES + tuple(
    (VOICE_REVISION, f"languages/{LANGUAGE}/embeddings/{voice}.safetensors")
    for voice in VOICES
)

print(
    f"Downloading the public Pocket TTS {LANGUAGE} preset-voice model and {len(VOICES)} Iris profiles.",
    flush=True,
)
for revision, filename in files:
    print(f"  {filename}", flush=True)
    hf_hub_download(
        repo_id=MODEL_REPO,
        filename=filename,
        revision=revision,
        token=False,
    )
print("Pocket TTS model files are ready. No cloning weights or credentials were used.", flush=True)
