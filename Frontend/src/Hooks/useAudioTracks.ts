import { useEffect, useRef, useState, useCallback } from 'react';
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
  setTrackVolume: (index: number, volume: number) => void;
  toggleTrackMute: (index: number) => void;
  setMutedTracks: (muted: Set<number>) => void;
  toggleSolo: (index: number) => void;
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

        // Exclude track 0 (Full Mix) since it doubles individual tracks
        const individualTracks = data.filter((t) => t.index > 0);
        if (cancelled || individualTracks.length === 0) return;

        setTracks(individualTracks);

        const initialVolumes: Record<number, number> = {};
        for (const t of individualTracks) {
          initialVolumes[t.index] = 1;
        }
        setVolumes(initialVolumes);
        setMutedTracks(new Set());
        setSoloTrack(null);

        // Clean up existing elements before creating new ones
        cleanupAudioElements(audioElementsRef.current);

        const elements = new Map<number, HTMLAudioElement>();
        for (const track of individualTracks) {
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

  // Apply volume/mute/solo to audio elements
  useEffect(() => {
    for (const [index, audio] of audioElementsRef.current.entries()) {
      if (soloTrack !== null) {
        audio.muted = index !== soloTrack;
      } else {
        audio.muted = mutedTracks.has(index);
      }
      audio.volume = volumes[index] ?? 1;
    }
  }, [volumes, mutedTracks, soloTrack, tracks]);

  const setTrackVolume = useCallback((index: number, volume: number) => {
    const clamped = Math.max(0, Math.min(1, volume));
    setVolumes((prev) => ({ ...prev, [index]: clamped }));
  }, []);

  const toggleTrackMute = useCallback((index: number) => {
    setMutedTracks((prev) => {
      const next = new Set(prev);
      if (next.has(index)) {
        next.delete(index);
      } else {
        next.add(index);
      }
      return next;
    });
  }, []);

  const replaceMutedTracks = useCallback((muted: Set<number>) => {
    setMutedTracks(muted);
  }, []);

  const toggleSolo = useCallback((index: number) => {
    setSoloTrack((prev) => (prev === index ? null : index));
  }, []);

  return {
    tracks,
    volumes,
    mutedTracks,
    soloTrack,
    setTrackVolume,
    toggleTrackMute,
    setMutedTracks: replaceMutedTracks,
    toggleSolo,
    isMultiTrack,
  };
}
