"""Generate ground crew clips via Together (Orpheus), trim + normalize, verify via Whisper.

Usage: TOGETHER_API_KEY=... python -I generate_groundcrew.py <out_dir> [voice ...]
       ONLY=name1,name2 limits the run to some phrases.
Writes <out_dir>/<voice>/<phrase>.wav, <out_dir>/phrases.json and <out_dir>/_report.json.
Raw takes land in <out_dir>/_raw (not meant to be committed).
"""
import array
import concurrent.futures as cf
import difflib
import json
import os
import re
import sys
import time
import urllib.request
import uuid
import wave

PHRASES = {
    "tug_connected_1": "Cockpit, ground. Tug is connected, bypass pin is in. Release parking brake, please.",
    "tug_connected_2": "Hello cockpit, ground here. Tow bar connected and steering pin inserted. You can release the brakes.",
    "push_start_1": "Brakes released, pushing back now.",
    "push_start_2": "Okay, commencing pushback.",
    "push_stopping_1": "Okay, stopping the push.",
    "push_stopping_2": "Coming to a stop.",
    "set_brake_1": "Pushback complete. Set parking brake, please.",
    "set_brake_2": "We're stopped. Set parking brake, please.",
    "disconnecting_1": "Parking brake set, thank you. Disconnecting now, stand by.",
    "disconnecting_2": "Copy, parking brake is set. Disconnecting the tug.",
    "disconnected_1": "Tug disconnected, bypass pin removed. Watch for my hand signal on the left. Have a good flight!",
    "disconnected_2": "All done, pin is out. Hand signal on your left. Bye bye!",
    "cancelled_1": "Copy, pushback cancelled. Disconnecting the tug.",
    "brake_during_push_1": "Whoa, brakes are set! Okay, we're stopped. Disconnecting the tug.",
    "facing_north": "Facing north.",
    "facing_northeast": "Facing north-east.",
    "facing_east": "Facing east.",
    "facing_southeast": "Facing south-east.",
    "facing_south": "Facing south.",
    "facing_southwest": "Facing south-west.",
    "facing_west": "Facing west.",
    "facing_northwest": "Facing north-west.",
    "tug_on_the_way_1": "Ground copies, tug is on the way.",
    "steer_tail_left_1": "Tail left.",
    "steer_tail_right_1": "Tail right.",
    "steer_straight_1": "Straight back.",
    "clear_to_start_1": "You're cleared to start engines.",
}

API = "https://api.together.ai/v1"
PAD_MS = 100
PEAK = 0.89  # about -1 dBFS
MIN_SCORE = 0.9


def load_key():
    key = os.environ.get("TOGETHER_API_KEY")
    if not key:
        raise SystemExit("set TOGETHER_API_KEY")
    return key


def tts(key, text, voice):
    body = json.dumps({
        "model": "canopylabs/orpheus-3b-0.1-ft",
        "input": text,
        "voice": voice,
        "response_format": "wav",
        "stream": False,
    }).encode()
    req = urllib.request.Request(f"{API}/audio/speech", data=body, headers={
        "Authorization": f"Bearer {key}", "Content-Type": "application/json", "User-Agent": "rampside-tts/1.0"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return r.read()


def transcribe(key, path):
    boundary = uuid.uuid4().hex
    data = open(path, "rb").read()
    parts = []
    for name, value in (("model", "openai/whisper-large-v3"), ("language", "en")):
        parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode())
    parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.wav\"\r\nContent-Type: audio/wav\r\n\r\n".encode())
    parts.append(data + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    req = urllib.request.Request(f"{API}/audio/transcriptions", data=b"".join(parts), headers={
        "Authorization": f"Bearer {key}", "Content-Type": f"multipart/form-data; boundary={boundary}", "User-Agent": "rampside-tts/1.0"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read())["text"]


def process(raw_path, out_path):
    with wave.open(raw_path, "rb") as w:
        rate, ch, width = w.getframerate(), w.getnchannels(), w.getsampwidth()
        assert ch == 1 and width == 2, (ch, width)
        s = array.array("h", w.readframes(w.getnframes()))
    # Trim: first/last sample above ~-40 dBFS, keep PAD_MS around it.
    thr = 328
    idx = [i for i in range(len(s)) if abs(s[i]) > thr]
    if not idx:
        raise ValueError("silent clip")
    pad = rate * PAD_MS // 1000
    start, end = max(0, idx[0] - pad), min(len(s), idx[-1] + pad)
    s = s[start:end]
    # Short fades so the cut doesn't click.
    fade = min(len(s) // 4, rate * 10 // 1000)
    for i in range(fade):
        s[i] = int(s[i] * i / fade)
        s[-1 - i] = int(s[-1 - i] * i / fade)
    peak = max(abs(x) for x in s) or 1
    gain = PEAK * 32767 / peak
    s = array.array("h", (max(-32768, min(32767, int(x * gain))) for x in s))
    with wave.open(out_path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(s.tobytes())
    return len(s) / rate


def norm(t):
    # Letters only: "north-west", "north west" and "northwest" compare equal.
    return re.sub(r"[^a-z]", "", t.lower())


def job(key, out_dir, voice, name, text):
    raw_dir = os.path.join(out_dir, "_raw", voice)
    os.makedirs(raw_dir, exist_ok=True)
    final_dir = os.path.join(out_dir, voice)
    os.makedirs(final_dir, exist_ok=True)
    final = os.path.join(final_dir, name + ".wav")
    best = None
    for attempt in range(4):
        raw = os.path.join(raw_dir, f"{name}.try{attempt}.wav")
        cand = os.path.join(raw_dir, f"{name}.try{attempt}.out.wav")
        try:
            open(raw, "wb").write(tts(key, text, voice))
            dur = process(raw, cand)
            heard = transcribe(key, cand)
            score = difflib.SequenceMatcher(None, norm(text), norm(heard)).ratio()
            if best is None or score > best[0]:
                best = (score, dur, heard, attempt + 1)
                os.replace(cand, final)
            if score >= MIN_SCORE:
                break
        except Exception as e:  # retry on API hiccups
            if best is None:
                best = (0, 0, f"ERROR {e}", attempt + 1)
            time.sleep(2)
    return voice, name, best


def main():
    key = load_key()
    out_dir = sys.argv[1]
    os.makedirs(out_dir, exist_ok=True)
    json.dump(PHRASES, open(os.path.join(out_dir, "phrases.json"), "w", encoding="utf-8"), indent=2)
    voices = sys.argv[2:] or ["leo", "dan", "zac", "tara"]
    only = [n for n in os.environ.get("ONLY", "").split(",") if n]
    phrases = {n: t for n, t in PHRASES.items() if not only or n in only}
    results = []
    with cf.ThreadPoolExecutor(6) as ex:
        futs = [ex.submit(job, key, out_dir, v, n, t) for v in voices for n, t in phrases.items()]
        for f in cf.as_completed(futs):
            v, n, (score, dur, heard, tries) = f.result()
            results.append({"voice": v, "name": n, "score": round(score, 2), "seconds": round(dur, 2), "tries": tries, "heard": heard})
            flag = "OK " if score >= MIN_SCORE else "BAD"
            print(f"{flag} {v:5} {n:22} {score:.2f} {dur:5.2f}s x{tries}  {heard}", flush=True)
    json.dump(sorted(results, key=lambda r: (r["voice"], r["name"])), open(os.path.join(out_dir, "_report.json"), "w"), indent=1)


if __name__ == "__main__":
    main()
