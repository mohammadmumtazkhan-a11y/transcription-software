import argparse
import json
import os
import sys

def run_transcription(audio_path, model_size="small", task="translate", glossary=None, output_path=None):
    if not os.path.exists(audio_path):
        print(f"Error: audio file {audio_path} does not exist", file=sys.stderr)
        sys.exit(1)

    try:
        from faster_whisper import WhisperModel
    except ImportError:
        print("Error: faster_whisper is not installed in Python environment.", file=sys.stderr)
        sys.exit(2)

    # Initial prompt biasing using glossary terms
    initial_prompt = None
    if glossary:
        initial_prompt = "Domain Glossary: " + ", ".join(glossary)

    # Load model on CPU with INT8 quantization (optimized for Intel Core i5 AVX-512 / VNNI)
    print(f"Loading faster-whisper model '{model_size}' on CPU (int8)...", file=sys.stderr)
    model = WhisperModel(model_size, device="cpu", compute_type="int8", cpu_threads=4)

    print(f"Transcribing '{audio_path}' with task='{task}'...", file=sys.stderr)
    segments, info = model.transcribe(
        audio_path,
        task=task,
        initial_prompt=initial_prompt,
        beam_size=5,
        word_timestamps=False
    )

    results = []
    for seg in segments:
        text = seg.text.strip()
        if text:
            results.append({
                "start": round(seg.start, 2),
                "end": round(seg.end, 2),
                "text": text,
                "avg_logprob": round(seg.avg_logprob, 3),
                "compression_ratio": round(seg.compression_ratio, 3),
                "no_speech_prob": round(seg.no_speech_prob, 3)
            })

    output_data = {
        "language": info.language,
        "language_probability": round(info.language_probability, 3),
        "duration": round(info.duration, 2),
        "segments": results
    }

    if output_path:
        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(output_data, f, indent=2, ensure_ascii=False)
        print(f"Saved {len(results)} segments to {output_path}", file=sys.stderr)
    else:
        print(json.dumps(output_data, ensure_ascii=False))

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Offline Speech Transcriber using faster-whisper")
    parser.add_argument("--audio", required=True, help="Path to input audio file")
    parser.add_argument("--model", default="small", choices=["tiny", "base", "small", "medium", "large-v3"], help="Whisper model size")
    parser.add_argument("--task", default="translate", choices=["transcribe", "translate"], help="Task: translate to English or transcribe verbatim")
    parser.add_argument("--glossary", nargs="*", default=[], help="Domain glossary terms")
    parser.add_argument("--output", help="Path to save JSON output")

    args = parser.parse_args()
    run_transcription(args.audio, args.model, args.task, args.glossary, args.output)
