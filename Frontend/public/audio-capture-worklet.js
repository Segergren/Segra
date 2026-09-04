// Segra audio-capture worklet.
//
// Taps the WebAudio playback graph (per-track mix) and forwards the exact mixed PCM to Segra's
// native audio stream server (ws://localhost:44031/), where it is played through the Segra
// process itself. That makes the WASAPI audio session belong to Segra.exe instead of
// msedgewebview2.exe, so Discord/OBS "application audio" capture picks it up.
//
// The node is input-only: it does NOT pass audio through to the context destination, because
// the native path is the one audible path when this tap is active (a passthrough would double
// the sound with a ~40-100ms echo).
//
// This file is plain JS on purpose: it ships via the Vite `public/` directory and is loaded by
// `AudioContext.audioWorklet.addModule()`, which requires a standalone module with no imports.

const BATCH_FRAMES = 2048;

class SegraAudioCaptureProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.batch = new Float32Array(BATCH_FRAMES * 2);
    this.frames = 0;
  }

  process(inputs) {
    const input = inputs[0];

    // Accumulate interleaved stereo PCM and ship it in bounded batches.
    if (input && input[0]) {
      const left = input[0];
      const right = input[1] || left;
      const n = Math.min(left.length, BATCH_FRAMES * 4);
      for (let i = 0; i < n; i++) {
        this.batch[this.frames * 2] = left[i];
        this.batch[this.frames * 2 + 1] = right[i];
        this.frames++;
        if (this.frames === BATCH_FRAMES) {
          const block = this.batch;
          this.batch = new Float32Array(BATCH_FRAMES * 2);
          this.frames = 0;
          this.port.postMessage(block.buffer, [block.buffer]);
        }
      }
    }
    return true;
  }
}

registerProcessor('segra-audio-capture', SegraAudioCaptureProcessor);
