# Beeswax's local voice

This service uses a youthful, childlike Beeswax voice designed to stay in a comfortable middle register. FFmpeg plays each generated line at 1.1× its original pace and keeps the pitch. The reference was synthesized with Qwen3-TTS VoiceDesign.

Install the requirements in a Python environment with CUDA-enabled PyTorch and FFmpeg. Set `FFMPEG` if FFmpeg is outside PATH. Run `python tts-qwen/serve.py` from Hive. It listens only on loopback.

For a rehearsal on the same computer, set `VBS_LOCAL_TTS_URL=http://127.0.0.1:8243/speak` and `ASK_TTS_FIRST_CHUNK_MS=12000` in the Hive environment. The paid provider remains the default when the URL is empty. Selecting the local voice keeps generated replies on the PC and uses captions if it fails. Scripted clips stay on the existing path.

This setup renders a whole line before it can send audio. Short lines took roughly seven to ten seconds on the RTX 4070 in the standalone benchmark. The longer rehearsal wait lets you hear the voice. It is too slow for a booth run with a near-instant reply requirement.
