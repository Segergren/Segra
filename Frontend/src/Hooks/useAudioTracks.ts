import { useEffect, useLayoutEffect, useRef, useState, useCallback } from 'react';
import { createFile, MP4BoxBuffer } from 'mp4box';
import type { ISOFile } from 'mp4box';
import { Content } from '../Models/types';

export interface AudioTrackInfo {
  index: number;
  name: string;
}

export interface AudioTrackState {
  tracks: AudioTrackInfo[];
  volumes: Record<number, number>;
  mutedTracks: Set<number>;
  soloTrack: number | null;
  masterMuted: boolean;
  masterVolume: number;
  setTrackVolume: (index: number, volume: number) => void;
  toggleTrackMute: (index: number) => void;
  setMutedTracks: (muted: Set<number>) => void;
  toggleSolo: (index: number) => void;
  setMasterMuted: (muted: boolean) => void;
  setMasterVolume: (vol: number) => void;
  setMuteOverride: (mutedIndices: number[] | null) => void;
  setVolumeOverride: (volumes: Record<number, number> | null) => void;
  isMultiTrack: boolean;
}

interface AudioTrackData {
  segraIndex: number;
  mp4TrackId: number;
  timescale: number;
  // AAC encoder priming (media_time from the edit list) in microseconds.
  // Subtracted from every decoded timestamp so presentation time starts at 0.
  primingMicros: number;
  offsets: Float64Array;
  sizes: Float64Array;
  cts: Float64Array;
  sampleCount: number;
  cursor: number;
  decoder: AudioDecoder;
  decoderConfig: AudioDecoderConfig;
  gainNode: GainNode;
  scheduledSources: Set<AudioBufferSourceNode>;
}

const LOOKAHEAD_SECONDS = 2;
const CHUNK_SIZE = 2 * 1024 * 1024;
const SAMPLES_PER_BATCH = 100;

function makeMp4BoxBuffer(data: ArrayBuffer, fileStart: number): MP4BoxBuffer {
  return MP4BoxBuffer.fromArrayBuffer(data, fileStart);
}

// Walk esds -> DecoderSpecificInfo.data to obtain the raw AudioSpecificConfig.
// AAC streams in MP4 require this as the AudioDecoder `description`.
function extractAudioSpecificConfig(entry: unknown): Uint8Array | undefined {
  try {
    const e = entry as {
      esds?: { esd?: { descs?: Array<{ descs?: Array<{ data?: Uint8Array }> }> } };
    };
    const dsi = e.esds?.esd?.descs?.[0]?.descs?.[0];
    return dsi?.data ? new Uint8Array(dsi.data) : undefined;
  } catch {
    return undefined;
  }
}

function computePrimingMicros(
  edits: Array<{ media_time: number; segment_duration: number }> | undefined,
  trackTimescale: number,
): number {
  if (!edits || edits.length === 0) return 0;
  const first = edits[0];
  if (first.media_time > 0 && trackTimescale > 0) {
    return (first.media_time / trackTimescale) * 1_000_000;
  }
  return 0;
}

function seekCursor(td: AudioTrackData, timeSec: number): number {
  const targetRaw = (timeSec + td.primingMicros / 1_000_000) * td.timescale;
  let lo = 0;
  let hi = td.sampleCount;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (td.cts[mid] < targetRaw) lo = mid + 1;
    else hi = mid;
  }
  return Math.max(0, lo - 1);
}

export function useAudioTracks(
  videoRef: React.RefObject<HTMLVideoElement | null>,
  video: Content,
): AudioTrackState {
  const [tracks, setTracks] = useState<AudioTrackInfo[]>([]);
  const [volumes, setVolumes] = useState<Record<number, number>>({});
  const [mutedTracks, setMutedTracks] = useState<Set<number>>(new Set());
  const [soloTrack, setSoloTrack] = useState<number | null>(null);
  const [masterMuted, setMasterMutedState] = useState(false);
  const [masterVolume, setMasterVolumeState] = useState(1);
  const isMultiTrack = tracks.length > 1;

  const audioCtxRef = useRef<AudioContext | null>(null);
  const masterGainRef = useRef<GainNode | null>(null);
  const trackDataRef = useRef<Map<number, AudioTrackData>>(new Map());

  const fileSizeRef = useRef<number>(0);
  const fetchUrlRef = useRef<string>('');
  const pumpingRef = useRef<boolean>(false);
  const abortRef = useRef<AbortController | null>(null);

  const audioStartCtxTimeRef = useRef<number>(0);
  const playbackRateRef = useRef<number>(1);
  // Bumped on every seek/ratechange. Used to drop stale in-flight fetches.
  const generationRef = useRef<number>(0);

  const masterMutedRef = useRef(false);
  const masterVolumeRef = useRef(1);
  const muteOverrideRef = useRef<Set<number> | null>(null);
  const volumeOverrideRef = useRef<Record<number, number> | null>(null);
  const latestRef = useRef({ mutedTracks, soloTrack, volumes });
  useLayoutEffect(() => {
    latestRef.current = { mutedTracks, soloTrack, volumes };
  });

  const applyMuting = useCallback(() => {
    const ctx = audioCtxRef.current;
    const master = masterGainRef.current;
    if (!ctx || !master) return;
    const now = ctx.currentTime;
    const { mutedTracks: defaultMuted, soloTrack: solo, volumes: vols } = latestRef.current;
    const effectiveMuted = muteOverrideRef.current ?? defaultMuted;
    const effectiveVolumes = volumeOverrideRef.current ?? vols;

    master.gain.setTargetAtTime(masterMutedRef.current ? 0 : masterVolumeRef.current, now, 0.005);

    for (const td of trackDataRef.current.values()) {
      let muted: boolean;
      if (solo !== null) {
        muted = td.segraIndex !== solo;
      } else {
        muted = effectiveMuted.has(td.segraIndex);
      }
      const vol = effectiveVolumes[td.segraIndex] ?? 1;
      td.gainNode.gain.setTargetAtTime(muted ? 0 : vol, now, 0.005);
    }
  }, []);

  const stopAllSources = useCallback(() => {
    for (const td of trackDataRef.current.values()) {
      for (const source of td.scheduledSources) {
        try {
          source.onended = null;
          source.stop();
        } catch {
          // ignore
        }
        try {
          source.disconnect();
        } catch {
          // ignore
        }
      }
      td.scheduledSources.clear();
    }
  }, []);

  const rangeFetch = useCallback(
    async (start: number, end: number, signal: AbortSignal): Promise<ArrayBuffer> => {
      const res = await fetch(fetchUrlRef.current, {
        headers: { Range: `bytes=${start}-${end}` },
        signal,
      });
      if (!res.ok && res.status !== 206 && res.status !== 200) {
        throw new Error(`range fetch ${start}-${end} failed: ${res.status}`);
      }
      return res.arrayBuffer();
    },
    [],
  );

  const onDecoderOutput = useCallback(
    (td: AudioTrackData, data: AudioData) => {
      const ctx = audioCtxRef.current;
      const vid = videoRef.current;
      if (!ctx) {
        data.close();
        return;
      }
      if (vid?.paused) {
        data.close();
        return;
      }

      const tsMicros = data.timestamp;
      const numFrames = data.numberOfFrames;
      const numChannels = data.numberOfChannels;
      const sampleRate = data.sampleRate;

      let audioBuffer: AudioBuffer;
      try {
        audioBuffer = ctx.createBuffer(numChannels, numFrames, sampleRate);
        for (let ch = 0; ch < numChannels; ch++) {
          data.copyTo(audioBuffer.getChannelData(ch), { planeIndex: ch, format: 'f32-planar' });
        }
      } catch {
        try {
          audioBuffer = ctx.createBuffer(numChannels, numFrames, sampleRate);
          for (let ch = 0; ch < numChannels; ch++) {
            data.copyTo(audioBuffer.getChannelData(ch), { planeIndex: ch });
          }
        } catch {
          data.close();
          return;
        }
      }
      data.close();

      const presSeconds = (tsMicros - td.primingMicros) / 1_000_000;
      const rate = playbackRateRef.current || 1;
      const playAt = audioStartCtxTimeRef.current + presSeconds / rate;
      const now = ctx.currentTime;

      if (playAt < now - 0.02) return;

      const source = ctx.createBufferSource();
      source.buffer = audioBuffer;
      source.playbackRate.value = rate;
      source.connect(td.gainNode);

      const startAt = Math.max(playAt, now);
      const offsetInBuffer = startAt > playAt ? startAt - playAt : 0;
      try {
        source.start(startAt, offsetInBuffer);
      } catch {
        return;
      }
      td.scheduledSources.add(source);
      source.onended = () => {
        td.scheduledSources.delete(source);
        try {
          source.disconnect();
        } catch {
          // ignore
        }
      };
    },
    [videoRef],
  );

  // Fetches sample bytes and feeds the decoders until the lookahead window is
  // satisfied. Serialized via pumpingRef. On mid-fetch seek the generation bumps
  // and the pump drops the in-flight batch before the next iteration.
  const pumpDecoders = useCallback(async () => {
    if (pumpingRef.current) return;
    const ctx = audioCtxRef.current;
    const vid = videoRef.current;
    const signal = abortRef.current?.signal;
    if (!ctx || !vid || !signal || signal.aborted) return;
    if (vid.paused) return;

    pumpingRef.current = true;
    try {
      let startGen = generationRef.current;
      while (!signal.aborted && !vid.paused) {
        if (generationRef.current !== startGen) startGen = generationRef.current;

        const playhead = vid.currentTime;
        const rate = playbackRateRef.current || 1;

        let target: AudioTrackData | null = null;
        let minAhead = LOOKAHEAD_SECONDS;
        for (const td of trackDataRef.current.values()) {
          if (td.cursor >= td.sampleCount) continue;
          const nextCtsSec = td.cts[td.cursor] / td.timescale - td.primingMicros / 1_000_000;
          const ahead = (nextCtsSec - playhead) / rate;
          if (ahead >= LOOKAHEAD_SECONDS) continue;
          if (ahead < minAhead) {
            minAhead = ahead;
            target = td;
          }
        }

        if (!target) break;

        const batchStart = target.cursor;
        const batchEnd = Math.min(batchStart + SAMPLES_PER_BATCH, target.sampleCount);
        const rangeStart = target.offsets[batchStart];
        const rangeEnd = target.offsets[batchEnd - 1] + target.sizes[batchEnd - 1] - 1;

        let ab: ArrayBuffer;
        try {
          ab = await rangeFetch(rangeStart, rangeEnd, signal);
        } catch (err) {
          if ((err as { name?: string }).name === 'AbortError') return;
          console.warn('[useAudioTracks] pump fetch failed', err);
          return;
        }
        if (signal.aborted) return;
        if (generationRef.current !== startGen) continue;

        const view = new Uint8Array(ab);
        for (let i = batchStart; i < batchEnd; i++) {
          const localOffset = target.offsets[i] - rangeStart;
          const size = target.sizes[i];
          const sliceBytes = view.subarray(localOffset, localOffset + size);
          const tsMicros = Math.round((target.cts[i] / target.timescale) * 1_000_000);
          const nextRawCts =
            i + 1 < target.sampleCount
              ? target.cts[i + 1]
              : target.cts[i] +
                (i > 0 ? target.cts[i] - target.cts[i - 1] : target.timescale * 0.02);
          const durMicros = Math.max(
            1,
            Math.round(((nextRawCts - target.cts[i]) / target.timescale) * 1_000_000),
          );
          let chunk: EncodedAudioChunk;
          try {
            chunk = new EncodedAudioChunk({
              type: 'key',
              timestamp: tsMicros,
              duration: durMicros,
              data: sliceBytes,
            });
          } catch (err) {
            console.warn('[useAudioTracks] build chunk failed', err);
            continue;
          }
          try {
            target.decoder.decode(chunk);
          } catch (err) {
            console.warn('[useAudioTracks] decode failed', err);
          }
        }
        target.cursor = batchEnd;
      }
    } finally {
      pumpingRef.current = false;
    }
  }, [rangeFetch, videoRef]);

  const resyncTo = useCallback(
    (time: number, rate: number) => {
      const ctx = audioCtxRef.current;
      if (!ctx) return;

      generationRef.current += 1;
      stopAllSources();

      for (const td of trackDataRef.current.values()) {
        try {
          td.decoder.reset();
          td.decoder.configure(td.decoderConfig);
        } catch (err) {
          console.warn('[useAudioTracks] decoder reconfigure failed', err);
        }
        td.cursor = seekCursor(td, time);
      }

      audioStartCtxTimeRef.current = ctx.currentTime - time / rate;
      playbackRateRef.current = rate;

      pumpDecoders();
    },
    [pumpDecoders, stopAllSources],
  );

  useEffect(() => {
    if (!video.audioTrackNames || video.audioTrackNames.length <= 1) {
      setTracks([]);
      return;
    }
    if (typeof AudioDecoder === 'undefined' || typeof EncodedAudioChunk === 'undefined') {
      console.warn('[useAudioTracks] WebCodecs AudioDecoder not available');
      setTracks([]);
      return;
    }

    const abortController = new AbortController();
    abortRef.current = abortController;
    let cancelled = false;

    (async () => {
      try {
        const probe = await AudioDecoder.isConfigSupported({
          codec: 'mp4a.40.2',
          sampleRate: 48000,
          numberOfChannels: 2,
        });
        if (!probe.supported) {
          console.warn('[useAudioTracks] AAC-LC not supported by AudioDecoder');
          return;
        }
      } catch (err) {
        console.warn('[useAudioTracks] isConfigSupported failed', err);
        return;
      }
      if (cancelled) return;

      const ctx = new AudioContext({ sampleRate: 48000, latencyHint: 'interactive' });
      audioCtxRef.current = ctx;
      const master = ctx.createGain();
      master.gain.value = 1;
      master.connect(ctx.destination);
      masterGainRef.current = master;

      const url = `http://localhost:2222/api/content?input=${encodeURIComponent(video.filePath)}`;
      fetchUrlRef.current = url;

      // Probe file size via a tiny ranged GET. ContentServer exposes Content-Range
      // via Access-Control-Expose-Headers so we can read the total length here.
      let fileSize = -1;
      try {
        const probeRes = await fetch(url, {
          headers: { Range: 'bytes=0-1' },
          signal: abortController.signal,
        });
        const contentRange = probeRes.headers.get('Content-Range');
        const m = contentRange?.match(/\/(\d+)\s*$/);
        if (m) fileSize = parseInt(m[1], 10);
        await probeRes.arrayBuffer();
      } catch (err) {
        if ((err as { name?: string }).name !== 'AbortError') {
          console.error('[useAudioTracks] file size probe failed', err);
        }
        return;
      }
      if (cancelled || abortController.signal.aborted) return;
      if (fileSize <= 0) {
        console.error('[useAudioTracks] could not determine file size');
        return;
      }
      fileSizeRef.current = fileSize;

      // mp4box is used only as a moov parser. After we extract compact sample
      // tables we drop the file reference and manage fetching ourselves.
      let file: ISOFile | null = createFile();
      type AudioTrackInfoRaw = {
        id: number;
        codec: string;
        timescale: number;
        edits?: Array<{ media_time: number; segment_duration: number }>;
        audio?: { sample_rate: number; channel_count: number };
      };
      type MoovInfo = { audioTracks: Array<AudioTrackInfoRaw> };

      let readyFired = false;
      let readyInfo: MoovInfo | null = null;
      file.onReady = (info) => {
        readyFired = true;
        readyInfo = info as unknown as MoovInfo;
      };
      file.onError = (mod, msg) => {
        console.warn('[useAudioTracks] mp4box error', mod, msg);
      };

      // Seed mp4box with enough bytes to parse moov. Phase A seeds ftyp + mdat
      // header, phase B follows mp4box's nextParsePosition return value
      // (typically a jump straight to moov for moov-at-end files), phase C is a
      // sequential fallback for unusual layouts.
      const appendRange = async (start: number, end: number) => {
        if (end < start || start >= fileSize) return;
        const ab = await rangeFetch(start, end, abortController.signal);
        return file!.appendBuffer(makeMp4BoxBuffer(ab, start));
      };

      try {
        const phaseAEnd = Math.min(CHUNK_SIZE - 1, fileSize - 1);
        const retA = await appendRange(0, phaseAEnd);

        let nextOffset = CHUNK_SIZE;
        if (!readyFired && typeof retA === 'number' && retA > phaseAEnd + 1) {
          const PHASE_B_CAP = 128 * 1024 * 1024;
          const start = retA;
          const end = Math.min(start + PHASE_B_CAP - 1, fileSize - 1);
          const retB = await appendRange(start, end);
          if (typeof retB === 'number' && retB > end) nextOffset = retB;
          else nextOffset = end + 1;
        }

        if (!readyFired) {
          let iterations = 0;
          const MAX_SEED_ITERATIONS = 128;
          while (
            !readyFired &&
            nextOffset < fileSize &&
            iterations < MAX_SEED_ITERATIONS &&
            !cancelled &&
            !abortController.signal.aborted
          ) {
            const end = Math.min(nextOffset + CHUNK_SIZE - 1, fileSize - 1);
            const ret = await appendRange(nextOffset, end);
            if (readyFired) break;
            if (typeof ret === 'number' && ret > end) nextOffset = ret;
            else nextOffset = end + 1;
            iterations += 1;
          }
        }

        if (!readyFired) {
          console.error('[useAudioTracks] mp4box never parsed moov');
          return;
        }
      } catch (err) {
        if ((err as { name?: string }).name !== 'AbortError') {
          console.error('[useAudioTracks] seeding fetches failed', err);
        }
        return;
      }

      if (cancelled || abortController.signal.aborted) return;
      const info = readyInfo as MoovInfo | null;
      if (!info) return;

      const audioTracks = info.audioTracks ?? [];
      if (audioTracks.length === 0) {
        console.warn('[useAudioTracks] mp4 has no audio tracks');
        return;
      }

      const displayTracks: AudioTrackInfo[] = [];
      const initialVolumes: Record<number, number> = {};

      for (let i = 0; i < audioTracks.length; i++) {
        const trk = audioTracks[i];
        const name = video.audioTrackNames?.[i] ?? `Track ${i + 1}`;

        const trakBox = file.getTrackById(trk.id) as unknown as
          | {
              mdia?: { minf?: { stbl?: { stsd?: { entries?: unknown[] } } } };
              samples?: Array<{ offset: number; size: number; cts: number }>;
            }
          | undefined;
        const stsdEntry = trakBox?.mdia?.minf?.stbl?.stsd?.entries?.[0];
        const description = extractAudioSpecificConfig(stsdEntry);

        const decoderConfig: AudioDecoderConfig = {
          codec: trk.codec,
          sampleRate: trk.audio?.sample_rate ?? 48000,
          numberOfChannels: trk.audio?.channel_count ?? 2,
          ...(description ? { description } : {}),
        };

        const support = await AudioDecoder.isConfigSupported(decoderConfig).catch(() => ({
          supported: false,
        }));
        if (!support.supported) {
          console.warn(`[useAudioTracks] track ${i} (${trk.codec}) unsupported`);
          continue;
        }

        const rawSamples = trakBox?.samples ?? [];
        const sampleCount = rawSamples.length;
        if (sampleCount === 0) {
          console.warn(`[useAudioTracks] track ${i} has no samples`);
          continue;
        }
        const offsets = new Float64Array(sampleCount);
        const sizes = new Float64Array(sampleCount);
        const cts = new Float64Array(sampleCount);
        for (let s = 0; s < sampleCount; s++) {
          const sample = rawSamples[s];
          offsets[s] = sample.offset;
          sizes[s] = sample.size;
          cts[s] = sample.cts;
        }

        const gain = ctx.createGain();
        gain.gain.value = 1;
        gain.connect(master);

        const td: AudioTrackData = {
          segraIndex: i,
          mp4TrackId: trk.id,
          timescale: trk.timescale,
          primingMicros: computePrimingMicros(trk.edits, trk.timescale),
          offsets,
          sizes,
          cts,
          sampleCount,
          cursor: 0,
          decoder: null as unknown as AudioDecoder,
          decoderConfig,
          gainNode: gain,
          scheduledSources: new Set(),
        };

        const decoder = new AudioDecoder({
          output: (d) => onDecoderOutput(td, d),
          error: (e) => console.warn(`[useAudioTracks] decoder error track ${i}`, e),
        });
        try {
          decoder.configure(decoderConfig);
        } catch (err) {
          console.warn(`[useAudioTracks] decoder.configure failed track ${i}`, err);
          continue;
        }
        td.decoder = decoder;

        trackDataRef.current.set(i, td);
        displayTracks.push({ index: i, name });
        initialVolumes[i] = 1;
      }

      if (trackDataRef.current.size === 0) {
        console.warn('[useAudioTracks] no decodable audio tracks');
        return;
      }

      // Drop mp4box so its internal sample object arrays can be GC'd.
      file = null;

      setTracks(displayTracks);
      setVolumes(initialVolumes);
      setMutedTracks(new Set([0]));
      setSoloTrack(null);

      const vid = videoRef.current;
      const startTime = vid?.currentTime ?? 0;
      const startRate = vid?.playbackRate ?? 1;
      for (const td of trackDataRef.current.values()) {
        td.cursor = seekCursor(td, startTime);
      }
      audioStartCtxTimeRef.current = ctx.currentTime - startTime / startRate;
      playbackRateRef.current = startRate;
      pumpDecoders();
    })();

    return () => {
      cancelled = true;
      abortController.abort();
      generationRef.current += 1;

      stopAllSources();
      for (const td of trackDataRef.current.values()) {
        try {
          td.decoder.close();
        } catch {
          // ignore
        }
        try {
          td.gainNode.disconnect();
        } catch {
          // ignore
        }
      }
      trackDataRef.current.clear();

      const master = masterGainRef.current;
      if (master) {
        try {
          master.disconnect();
        } catch {
          // ignore
        }
      }
      masterGainRef.current = null;

      const ctx = audioCtxRef.current;
      if (ctx) ctx.close().catch(() => {});
      audioCtxRef.current = null;

      fileSizeRef.current = 0;
      pumpingRef.current = false;
      setTracks([]);
    };
  }, [
    video.filePath,
    video.audioTrackNames?.join('|'),
    pumpDecoders,
    onDecoderOutput,
    rangeFetch,
    stopAllSources,
    videoRef,
  ]);

  useEffect(() => {
    const vid = videoRef.current;
    if (!vid || !isMultiTrack) return;

    vid.muted = true;

    const onPlay = async () => {
      const ctx = audioCtxRef.current;
      if (!ctx) return;
      if (ctx.state === 'suspended') {
        try {
          await ctx.resume();
        } catch {
          // ignore
        }
      }
      resyncTo(vid.currentTime, vid.playbackRate);
    };

    const onPause = () => {
      stopAllSources();
    };

    const onSeeked = () => {
      resyncTo(vid.currentTime, vid.playbackRate);
    };

    const onRateChange = () => {
      resyncTo(vid.currentTime, vid.playbackRate);
    };

    const onTimeUpdate = () => {
      pumpDecoders();
    };

    vid.addEventListener('play', onPlay);
    vid.addEventListener('pause', onPause);
    vid.addEventListener('seeked', onSeeked);
    vid.addEventListener('ratechange', onRateChange);
    vid.addEventListener('timeupdate', onTimeUpdate);

    if (!vid.paused) onPlay();

    return () => {
      vid.removeEventListener('play', onPlay);
      vid.removeEventListener('pause', onPause);
      vid.removeEventListener('seeked', onSeeked);
      vid.removeEventListener('ratechange', onRateChange);
      vid.removeEventListener('timeupdate', onTimeUpdate);
      stopAllSources();
      vid.muted = localStorage.getItem('segra-muted') === 'true';
    };
  }, [videoRef, isMultiTrack, resyncTo, stopAllSources, pumpDecoders]);

  useEffect(() => {
    applyMuting();
  }, [volumes, mutedTracks, soloTrack, tracks, applyMuting]);

  const setTrackVolume = useCallback((index: number, volume: number) => {
    const clamped = Math.max(0, Math.min(1, volume));
    setVolumes((prev) => ({ ...prev, [index]: clamped }));
  }, []);

  const toggleTrackMute = useCallback((index: number) => {
    setMutedTracks((prev) => {
      const wasEnabled = !prev.has(index);
      if (wasEnabled) {
        const next = new Set(prev);
        next.add(index);
        return next;
      }
      if (index === 0) {
        const next = new Set<number>();
        for (const td of trackDataRef.current.values()) {
          if (td.segraIndex !== 0) next.add(td.segraIndex);
        }
        return next;
      }
      const next = new Set(prev);
      next.delete(index);
      next.add(0);
      return next;
    });
  }, []);

  const replaceMutedTracks = useCallback((muted: Set<number>) => {
    setMutedTracks(muted);
  }, []);

  const toggleSolo = useCallback((index: number) => {
    setSoloTrack((prev) => (prev === index ? null : index));
  }, []);

  const setMasterMuted = useCallback(
    (muted: boolean) => {
      masterMutedRef.current = muted;
      setMasterMutedState(muted);
      applyMuting();
    },
    [applyMuting],
  );

  const setMasterVolume = useCallback(
    (vol: number) => {
      const clamped = Math.max(0, Math.min(1, vol));
      masterVolumeRef.current = clamped;
      setMasterVolumeState(clamped);
      applyMuting();
    },
    [applyMuting],
  );

  const setMuteOverride = useCallback(
    (mutedIndices: number[] | null) => {
      muteOverrideRef.current = mutedIndices ? new Set(mutedIndices) : null;
      applyMuting();
    },
    [applyMuting],
  );

  const setVolumeOverride = useCallback(
    (vols: Record<number, number> | null) => {
      volumeOverrideRef.current = vols;
      applyMuting();
    },
    [applyMuting],
  );

  return {
    tracks,
    volumes,
    mutedTracks,
    soloTrack,
    masterMuted,
    masterVolume,
    setTrackVolume,
    toggleTrackMute,
    setMutedTracks: replaceMutedTracks,
    toggleSolo,
    setMasterMuted,
    setMasterVolume,
    setMuteOverride,
    setVolumeOverride,
    isMultiTrack,
  };
}
