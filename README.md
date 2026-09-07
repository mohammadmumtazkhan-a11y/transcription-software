# Personal Windows 11 Audio/Video Transcription Application

A dedicated Windows 11 desktop transcription and review tool engineered for an IT Business Analyst. It transcribes and translates complex stakeholder recordings (English, Hinglish, and Urdu) into faithful, readable, professionally formatted English documents (`.docx`).

## Key Features & Highlights

- **Platform:** Windows 11 (64-bit) native desktop application (.NET 8 WPF).
- **Target Hardware:** Optimized for Intel Core i5-1135G7 (4 cores, 16 GB RAM, Iris Xe graphics) with zero discrete GPU dependency.
- **Speech Capabilities:** High-fidelity speech recognition and direct translation of code-switched English/Hinglish/Urdu speech.
- **Budget Control:** Real-time persistent local cost ledger with hard stop enforcement at **USD 20.00/month**.
- **Performance SLA:** Processes up to 3 hours of audio in under 1 hour (Cloud-first turnaround ~4 to 6 minutes via Groq Whisper Large-v3).
- **Synchronized Editor:** Interactive sentence click-to-play audio alignment preserving original acoustic anchors even across manual text edits.
- **Export:** Executive-ready formatted Microsoft Word (`.docx`) documents with discrete timestamps and highlighted unresolved speech markers.
- **Disaster Recovery:** Portable, standalone executable installer (`Setup.exe`) ready for Google Drive backup and zero-dependency reinstallation on fresh Windows 11 machines.

## Documentation

- Full Architecture & Specifications: See [`Transcription_App_Specification.md`](Transcription_App_Specification.md).

## Technology Stack

- **Desktop Framework:** C# .NET 8 WPF
- **Audio Processing:** Bundled static FFmpeg & Silero VAD (ONNX Runtime)
- **Speech Engine:** Groq Cloud API (`whisper-large-v3` + `llama-3.1-8b-instant`) with local `faster-whisper` (CTranslate2 INT8 via OpenVINO) offline fallback
- **Data Persistence:** SQLite (`System.Data.SQLite`)
- **Document Generation:** OpenXML SDK (`DocumentFormat.OpenXml`)
- **Installer:** Inno Setup 6
