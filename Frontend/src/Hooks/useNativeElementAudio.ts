import { useEffect, useRef } from 'react';
import { Content } from '../Models/types';
import {
  createNativeCaptureTap,
  getNativeAudioSink,
  type NativeAudioSink,
} from '../Services/nativeAudioSink';

// createMediaElementSource can only be called once per element; guard double-sourcing
// (e.g. React StrictMode's mount/unmount/mount dev cycle).
const sourcedElements = new WeakSet<HTMLMediaElement>();

/**
 * Routes a single-audio-track <video> element's own audio through the native sink (played by
 * Segra.exe) so Windows application-audio capture (Discord/OBS) includes it. The element stays
 * the playback clock: its own A/V sync, volume, mute and playbackRate continue to apply, so no
 * scheduling logic is needed here. Multi-track content is left to useAudioTracks, and when the
 * native sink is unavailable the element keeps its default webview output.
 *
 * Note: the element's volume/mute are applied by Chromium's media pipeline upstream of the
 * MediaElementAudioSourceNode, which is what keeps the volume slider working through this path.
 */
export function useNativeElementAudio(
  videoRef: React.RefObject<HTMLVideoElement | null>,
  video: Content,
): void {
  const sinkRef = useRef<NativeAudioSink | null>(null);
  const ctxRef = useRef<AudioContext | null>(null);
  const nodeRef = useRef<AudioWorkletNode | null>(null);

  useEffect(() => {
    const vid = videoRef.current;
    if (!vid) return;

    // Multi-track (>= 2) is handled by useAudioTracks' WebCodecs graph; no audio track at all
    // needs no capture. Only exactly-one-track content takes the element path.
    if ((video.audioTrackNames?.length ?? 0) !== 1) return;
    if (sourcedElements.has(vid)) return;

    let cancelled = false;
    let sink: NativeAudioSink | null = null;

    const ensureRunning = () => {
      const ctx = ctxRef.current;
      if (ctx && ctx.state === 'suspended') {
        ctx.resume().catch(() => {});
      }
    };
    const startNative = () => {
      try {
        sink?.play(ctxRef.current?.sampleRate ?? 48000, 2);
      } catch {
        // ignore
      }
    };
    const stopNative = () => {
      try {
        sink?.flush();
      } catch {
        // ignore
      }
    };

    const onPlay = () => {
      ensureRunning();
      startNative();
    };
    const onPause = () => {
      stopNative();
    };
    const onVolumeChange = () => {
      // Unmuting counts as user activation; gives the context a resume opportunity.
      ensureRunning();
    };
    const onGesture = () => {
      ensureRunning();
    };

    (async () => {
      try {
        sink = await getNativeAudioSink();
        if (cancelled || !sink) return;

        const ctx = new AudioContext({ sampleRate: 48000, latencyHint: 'interactive' });
        if (cancelled) {
          ctx.close().catch(() => {});
          return;
        }
        ctxRef.current = ctx;

        const tap = await createNativeCaptureTap(ctx);
        if (cancelled || !tap) {
          ctxRef.current = null;
          ctx.close().catch(() => {});
          return;
        }

        const source = ctx.createMediaElementSource(vid);
        source.connect(tap.node);

        nodeRef.current = tap.node;
        sinkRef.current = tap.sink;
        sink = tap.sink;
        sourcedElements.add(vid);

        vid.addEventListener('play', onPlay);
        vid.addEventListener('pause', onPause);
        vid.addEventListener('volumechange', onVolumeChange);
        window.addEventListener('pointerdown', onGesture);
        window.addEventListener('keydown', onGesture);

        if (!vid.paused) onPlay();
      } catch {
        // The element keeps its default webview output; nothing was rerouted.
      }
    })();

    return () => {
      cancelled = true;
      vid.removeEventListener('play', onPlay);
      vid.removeEventListener('pause', onPause);
      vid.removeEventListener('volumechange', onVolumeChange);
      window.removeEventListener('pointerdown', onGesture);
      window.removeEventListener('keydown', onGesture);
      stopNative();
      try {
        nodeRef.current?.disconnect();
      } catch {
        // ignore
      }
      try {
        nodeRef.current?.port?.close();
      } catch {
        // ignore
      }
      nodeRef.current = null;
      sinkRef.current = null;
      try {
        ctxRef.current?.close().catch(() => {});
      } catch {
        // ignore
      }
      ctxRef.current = null;
    };
  }, [videoRef, video.id, video.filePath, video.audioTrackNames?.join('|')]);
}
