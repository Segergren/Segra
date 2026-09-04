// Native audio sink: forwards the webview's playback graph PCM to Segra's audio stream server
// (ws://localhost:44031/), which plays it through the Segra process itself. Because the WASAPI
// audio session then belongs to Segra.exe - not msedgewebview2.exe - Discord/OBS capture of the
// Segra window ("application audio") includes the in-app clip sound.
//
// The endpoint is Windows-only (started by the backend under #if WINDOWS); everywhere else the
// connect fails fast and callers fall back to plain WebAudio rendering.

const SINK_URL = 'ws://localhost:44031/';
const WORKLET_URL = `${window.location.origin}/audio-capture-worklet.js`;
const CONNECT_TIMEOUT_MS = 1500;

export interface NativeAudioSink {
  /** Starts (or restarts) the native playback stream. Must be called before PCM is sent. */
  play(sampleRate: number, channels: number): void;
  /** Stops native playback and discards buffered PCM. */
  flush(): void;
  /** Queues an interleaved IEEE float32 PCM block. No-op unless playing. */
  sendPcm(interleaved: Float32Array): void;
}

let socket: WebSocket | null = null;
let socketPromise: Promise<NativeAudioSink | null> | null = null;
let playing = false;

function createSink(ws: WebSocket): NativeAudioSink {
  return {
    play(sampleRate, channels) {
      playing = true;
      if (ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'play', sampleRate, channels }));
      }
    },
    flush() {
      playing = false;
      if (ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'flush' }));
      }
    },
    sendPcm(interleaved) {
      if (playing && ws.readyState === WebSocket.OPEN) {
        // Copy into an exact-size plain ArrayBuffer (the Float32Array may alias a larger one).
        const out = new ArrayBuffer(interleaved.byteLength);
        new Float32Array(out).set(interleaved);
        ws.send(out);
      }
    },
  };
}

/**
 * Returns a shared sink connected to the native audio stream server, or null when the endpoint
 * is unavailable (Linux, or the backend hasn't started it).
 */
export function getNativeAudioSink(): Promise<NativeAudioSink | null> {
  if (socket && socket.readyState === WebSocket.OPEN) {
    return Promise.resolve(createSink(socket));
  }
  if (socketPromise) return socketPromise;

  socketPromise = new Promise((resolve) => {
    let settled = false;
    const done = (result: NativeAudioSink | null) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      if (result) {
        resolve(result);
      } else {
        socket = null;
        socketPromise = null;
        resolve(null);
      }
    };

    const timeout = window.setTimeout(() => {
      try {
        socket?.close();
      } catch {
        // ignore
      }
      done(null);
    }, CONNECT_TIMEOUT_MS);

    try {
      const ws = new WebSocket(SINK_URL);
      socket = ws;
      ws.binaryType = 'arraybuffer';
      ws.onopen = () => done(createSink(ws));
      ws.onerror = () => done(null);
      ws.onclose = () => {
        // Reset the shared state so a future attach (e.g. the next video opened) reconnects.
        playing = false;
        if (socket === ws) socket = null;
        if (socketPromise) socketPromise = null;
      };
    } catch {
      done(null);
    }
  });

  return socketPromise;
}

/**
 * Creates an input-only capture worklet whose PCM is forwarded to the native sink (the sole
 * audible path while attached). Returns null when native capture is unavailable or the worklet
 * can't be loaded. Callers connect their source node to `node` and are responsible for cleanup.
 */
export async function createNativeCaptureTap(
  ctx: AudioContext,
): Promise<{ node: AudioWorkletNode; sink: NativeAudioSink } | null> {
  try {
    if (!ctx.audioWorklet) return null;
    const sink = await getNativeAudioSink();
    if (!sink) return null;

    await ctx.audioWorklet.addModule(WORKLET_URL);

    const node = new AudioWorkletNode(ctx, 'segra-audio-capture', {
      numberOfInputs: 1,
      numberOfOutputs: 0,
      channelCount: 2,
      channelCountMode: 'explicit',
      channelInterpretation: 'speakers',
    });

    node.port.onmessage = (ev) => {
      const data = ev.data;
      if (data instanceof ArrayBuffer) {
        sink.sendPcm(new Float32Array(data));
      }
    };

    return { node, sink };
  } catch {
    return null;
  }
}

/**
 * Taps `sourceNode` with an input-only capture worklet whose PCM is forwarded to the native
 * sink (the sole audible path while attached). Returns null (leaving the caller to connect the
 * source directly to the context destination) when native capture is unavailable or the worklet
 * can't be loaded.
 */
export async function attachNativeCaptureTap(
  ctx: AudioContext,
  sourceNode: AudioNode,
): Promise<{ node: AudioWorkletNode; sink: NativeAudioSink } | null> {
  const tap = await createNativeCaptureTap(ctx);
  if (!tap) return null;

  try {
    sourceNode.connect(tap.node);
    return tap;
  } catch {
    try {
      tap.node.disconnect();
    } catch {
      // ignore
    }
    return null;
  }
}
