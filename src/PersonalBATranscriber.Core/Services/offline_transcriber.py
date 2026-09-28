import argparse
import json
import os
import sys

def run_transcription(audio_path, model_size="base", task="translate", glossary=None, output_path=None):
    if not os.path.exists(audio_path):
        print(f"Error: audio file {audio_path} does not exist", file=sys.stderr)
        sys.exit(1)

    try:
        from faster_whisper import WhisperModel
    except ImportError:
        print("Error: faster_whisper is not installed in Python environment.", file=sys.stderr)
        sys.exit(2)

    initial_prompt = None
    if glossary:
        initial_prompt = "Domain Glossary: " + ", ".join(glossary)

    # Load model on CPU with INT8 quantization
    print(f"STATUS:Loading faster-whisper model '{model_size}' on CPU (int8)...", flush=True)
    model = WhisperModel(model_size, device="cpu", compute_type="int8", cpu_threads=4)

    print(f"STATUS:Beginning transcription for '{os.path.basename(audio_path)}'...", flush=True)
    segments, info = model.transcribe(
        audio_path,
        task=task,
        initial_prompt=initial_prompt,
        beam_size=3, # optimized for faster CPU decoding
        # Word-level timestamps make each segment's start/end snap to the
        # first/last spoken word, so "play this sentence" starts and stops
        # exactly on the speech instead of on Whisper's coarse 30s-window guesses.
        word_timestamps=True
    )

    print(f"INFO:DURATION:{info.duration:.2f}", flush=True)
    print(f"INFO:LANGUAGE:{info.language}:{info.language_probability:.2f}", flush=True)

    results = []
    for seg in segments:
        text = seg.text.strip()
        if text:
            start, end = seg.start, seg.end
            words = [w for w in (seg.words or []) if w.word.strip()]
            if words:
                start, end = words[0].start, words[-1].end
            item = {
                "start": round(start, 2),
                "end": round(end, 2),
                "text": text,
                "avg_logprob": round(seg.avg_logprob, 3),
                "compression_ratio": round(seg.compression_ratio, 3),
                "no_speech_prob": round(seg.no_speech_prob, 3)
            }
            results.append(item)
            # Emit live segment stream to stdout
            print("SEGMENT:" + json.dumps(item, ensure_ascii=False), flush=True)

    output_data = {
        "language": info.language,
        "language_probability": round(info.language_probability, 3),
        "duration": round(info.duration, 2),
        "segments": results
    }

    if output_path:
        with open(output_path, "w", encoding="utf-8") as f:
            json.dump(output_data, f, indent=2, ensure_ascii=False)
        print(f"STATUS:Finished transcribing {len(results)} segments.", flush=True)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Offline Speech Transcriber using faster-whisper")
    parser.add_argument("--audio", required=True, help="Path to input audio file")
    parser.add_argument("--model", default="base", choices=["tiny", "base", "small", "medium", "large-v3"], help="Whisper model size")
    parser.add_argument("--task", default="translate", choices=["transcribe", "translate"], help="Task: translate to English or transcribe verbatim")
    parser.add_argument("--glossary", nargs="*", default=[], help="Domain glossary terms")
    parser.add_argument("--output", help="Path to save JSON output")

    args = parser.parse_args()
    run_transcription(args.audio, args.model, args.task, args.glossary, args.output)
