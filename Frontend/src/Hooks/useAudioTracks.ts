import { useEffect, useLayoutEffect, useRef, useState, useCallback } from 'react';
import { Content } from '../Models/types';

export interface AudioTrackInfo {
  index: number;
  name: string;
  url: string;
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

const SYNC_THRESHOLD = 0.15;
const SYNC_CHECK_INTERVAL = 500;

function cleanupAudioElements(elements: Map<number, HTMLAudioElement>) {
  for (const audio of elements.values()) {
    audio.pause();
    audio.src = '';
    audio.load();
  }
  elements.clear();
}

export function useAudioTracks(
  videoRef: React.RefObject<HTMLVideoElement | null>,
  video: Content,
): AudioTrackState {
  const audioElementsRef = useRef<Map<number, HTMLAudioElement>>(new Map());
  const [tracks, setTracks] = useState<AudioTrackInfo[]>([]);
  const [volumes, setVolumes] = useState<Record<number, number>>({});
  const [mutedTracks, setMutedTracks] = useState<Set<number>>(new Set());
  const [soloTrack, setSoloTrack] = useState<number | null>(null);
  const isMultiTrack = tracks.length > 1;

  // Fetch extracted audio track files from backend
  useEffect(() => {
    if (!video.audioTrackNames || video.audioTrackNames.length <= 1) {
      setTracks([]);
      return;
    }

    let cancelled = false;

    const fetchTracks = async () => {
      try {
        const url = `http://localhost:2222/api/audiotracks?input=${encodeURIComponent(video.filePath)}&type=${video.type}`;
        const res = await fetch(url);
        const data: AudioTrackInfo[] = await res.json();

        if (cancelled || data.length === 0) return;

        setTracks(data);

        const initialVolumes: Record<number, number> = {};
        for (const t of data) {
          initialVolumes[t.index] = 1;
        }
        setVolumes(initialVolumes);
        // Full Mix (track 0) is muted by default since individual tracks cover the same audio
        setMutedTracks(new Set([0]));
        setSoloTrack(null);

        // Clean up existing elements before creating new ones
        cleanupAudioElements(audioElementsRef.current);

        const elements = new Map<number, HTMLAudioElement>();
        for (const track of data) {
          const audio = new Audio();
          audio.src = `http://localhost:2222${track.url}`;
          audio.preload = 'auto';
          audio.volume = initialVolumes[track.index] ?? 1;
          elements.set(track.index, audio);
        }
        audioElementsRef.current = elements;
      } catch (err) {
        console.error('Failed to load audio tracks:', err);
      }
    };

    fetchTracks();

    return () => {
      cancelled = true;
      cleanupAudioElements(audioElementsRef.current);
      setTracks([]);
    };
  }, [video.filePath, video.audioTrackNames?.length]);

  // Sync audio elements with video: play/pause/seek/rate + mute video
  useEffect(() => {
    const vid = videoRef.current;
    if (!vid || !isMultiTrack) return;

    // Mute the video element here (not earlier) so that video.tsx's
    // initialization effect has already seen isMultiTrack=true and
    // skipped its own vid.muted = isMuted assignment.
    vid.muted = true;

    const syncTime = () => {
      const t = vid.currentTime;
      for (const audio of audioElementsRef.current.values()) {
        audio.currentTime = t;
      }
    };

    const onPlay = () => {
      syncTime();
      for (const audio of audioElementsRef.current.values()) {
        audio.play().catch(() => {});
      }
    };

    const onPause = () => {
      for (const audio of audioElementsRef.current.values()) {
        audio.pause();
      }
    };

    const onSeeked = () => {
      syncTime();
    };

    const onRateChange = () => {
      const rate = vid.playbackRate;
      for (const audio of audioElementsRef.current.values()) {
        audio.playbackRate = rate;
      }
    };

    vid.addEventListener('play', onPlay);
    vid.addEventListener('pause', onPause);
    vid.addEventListener('seeked', onSeeked);
    vid.addEventListener('ratechange', onRateChange);

    // Initial sync if already playing
    if (!vid.paused) {
      for (const audio of audioElementsRef.current.values()) {
        audio.playbackRate = vid.playbackRate;
        audio.currentTime = vid.currentTime;
        audio.play().catch(() => {});
      }
    }

    // Periodic drift correction
    const intervalId = setInterval(() => {
      if (vid.paused) return;
      const vidTime = vid.currentTime;
      for (const audio of audioElementsRef.current.values()) {
        const drift = Math.abs(audio.currentTime - vidTime);
        if (drift > SYNC_THRESHOLD) {
          audio.currentTime = vidTime;
        }
      }
    }, SYNC_CHECK_INTERVAL);

    return () => {
      vid.removeEventListener('play', onPlay);
      vid.removeEventListener('pause', onPause);
      vid.removeEventListener('seeked', onSeeked);
      vid.removeEventListener('ratechange', onRateChange);
      clearInterval(intervalId);

      // Restore video audio when multi-track is deactivated
      vid.muted = localStorage.getItem('segra-muted') === 'true';
    };
  }, [videoRef, isMultiTrack]);

  // Master mute/volume -- playback-only controls, don't affect per-track state or clip output.
  const masterMutedRef = useRef(false);
  const [masterMuted, setMasterMutedState] = useState(false);
  const masterVolumeRef = useRef(1);
  const [masterVolume, setMasterVolumeState] = useState(1);

  // Overrides for per-selection muting/volumes -- stored as refs so changes don't cause re-renders.
  // When set, audio elements use these instead of the default state.
  const muteOverrideRef = useRef<Set<number> | null>(null);
  const volumeOverrideRef = useRef<Record<number, number> | null>(null);

  // Keep latest state in refs so the stable setMuteOverride can read them
  const latestRef = useRef({ mutedTracks, soloTrack, volumes });
  useLayoutEffect(() => {
    latestRef.current = { mutedTracks, soloTrack, volumes };
  });

  const applyMuting = useCallback(() => {
    const { mutedTracks: defaultMuted, soloTrack: solo, volumes: vols } = latestRef.current;
    const effectiveMuted = muteOverrideRef.current ?? defaultMuted;
    const effectiveVolumes = volumeOverrideRef.current ?? vols;
    for (const [index, audio] of audioElementsRef.current.entries()) {
      if (masterMutedRef.current) {
        audio.muted = true;
      } else if (solo !== null) {
        audio.muted = index !== solo;
      } else {
        audio.muted = effectiveMuted.has(index);
      }
      audio.volume = (effectiveVolumes[index] ?? 1) * masterVolumeRef.current;
    }
  }, []);

  // Apply when React state changes
  useEffect(() => {
    applyMuting();
  }, [volumes, mutedTracks, soloTrack, tracks, applyMuting]);

  const setTrackVolume = useCallback((index: number, volume: number) => {
    const clamped = Math.max(0, Math.min(1, volume));
    setVolumes((prev) => ({ ...prev, [index]: clamped }));
  }, []);

  const toggleTrackMute = useCallback((index: number) => {
    setMutedTracks((prev) => {
      const wasEnabled = prev.has(index);
      if (wasEnabled) {
        // Enabling this track
        if (index === 0) {
          // Enabling Full Mix: mute all individual tracks
          const next = new Set<number>();
          for (const [i] of audioElementsRef.current) {
            if (i !== 0) next.add(i);
          }
          return next;
        } else {
          // Enabling an individual track: mute Full Mix
          const next = new Set(prev);
          next.delete(index);
          next.add(0);
          return next;
        }
      } else {
        // Muting this track
        const next = new Set(prev);
        next.add(index);
        return next;
      }
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

  // Stable function to set/clear a per-selection mute override.
  // Directly applies to audio elements without touching React state.
  const setMuteOverride = useCallback(
    (mutedIndices: number[] | null) => {
      muteOverrideRef.current = mutedIndices ? new Set(mutedIndices) : null;
      applyMuting();
    },
    [applyMuting],
  );

  // Stable function to set/clear a per-selection volume override.
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
