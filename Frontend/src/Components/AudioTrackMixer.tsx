import { Fragment, useEffect, useRef } from 'react';
import { MorphIcon } from 'morphicons/react';
import { AudioLines, Headphones, Mic, VolumeX } from 'lucide';
import type { AudioTrackType } from '../Models/types';

const TRACK_TYPE_ICONS = { mix: AudioLines, input: Mic, output: Headphones };

export const AUDIO_TRACK_MENU_CLASS =
  'p-1.5 min-w-64 rounded-xl border border-white/10 bg-black/80 backdrop-blur-md shadow-xl';

export interface AudioTrackMixerTrack {
  index: number;
  name: string;
  type?: AudioTrackType;
}

interface AudioTrackMixerProps {
  tracks: AudioTrackMixerTrack[];
  mutedTracks: ReadonlySet<number>;
  volumes: Record<number, number>;
  onToggleMute: (index: number) => void;
  // unmute is true when a muted track's slider was moved, which also enables the track
  onVolumeChange: (index: number, volume: number, unmute: boolean) => void;
  onClose?: () => void;
}

export default function AudioTrackMixer({
  tracks,
  mutedTracks,
  volumes,
  onToggleMute,
  onVolumeChange,
  onClose,
}: AudioTrackMixerProps) {
  // Dragging a slider and releasing elsewhere in the row must not toggle mute
  const sliderPressedRef = useRef(false);

  useEffect(() => {
    if (!onClose) return;
    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handleEscape);
    return () => document.removeEventListener('keydown', handleEscape);
  }, [onClose]);

  return (
    <>
      {tracks.map((track, i) => {
        const isMuted = mutedTracks.has(track.index);
        const vol = volumes[track.index] ?? 1;
        const setVol = (v: number) =>
          onVolumeChange(track.index, Math.max(0, Math.min(1, v)), isMuted);
        return (
          <Fragment key={track.index}>
            {i === 1 && tracks[0].index === 0 && (
              <div className="mx-2 my-1 border-t border-white/10" />
            )}
            <div
              onPointerDownCapture={() => (sliderPressedRef.current = false)}
              onClick={() => {
                if (!sliderPressedRef.current) onToggleMute(track.index);
              }}
              className={`flex items-center justify-between gap-3 px-2 py-1.5 rounded-lg cursor-pointer transition-colors hover:bg-white/10`}
            >
              <button
                type="button"
                onClick={(e) => {
                  e.stopPropagation();
                  onToggleMute(track.index);
                }}
                className={`flex items-center flex-1 gap-2 min-w-0 cursor-pointer transition-colors ${isMuted ? 'text-white/40 hover:text-white/70' : 'text-white'}`}
                aria-label={isMuted ? 'Unmute track' : 'Mute track'}
                aria-pressed={!isMuted}
              >
                <MorphIcon
                  icon={isMuted ? VolumeX : TRACK_TYPE_ICONS[track.type ?? 'mix']}
                  size={16}
                  spring="snappy"
                  className={`shrink-0 ${isMuted ? '' : 'text-accent'}`}
                />
                <span className="text-sm truncate select-none">
                  {track.name.replace(' (Default)', '')}
                </span>
              </button>
              <div className="flex items-center gap-2 shrink-0">
                <input
                  type="range"
                  min="0"
                  max="1"
                  step="0.02"
                  value={vol}
                  onChange={(e) => setVol(parseFloat(e.target.value))}
                  onWheel={(e) => setVol(vol + (e.deltaY < 0 ? 0.05 : -0.05))}
                  onPointerDown={() => (sliderPressedRef.current = true)}
                  onDoubleClick={() => setVol(1)}
                  onPointerUp={(e) => e.currentTarget.blur()}
                  className={`player-volume w-24 h-5 bg-center bg-no-repeat bg-[length:100%_3px] cursor-pointer ${isMuted ? 'is-muted' : ''}`}
                  style={{
                    backgroundImage: `linear-gradient(to right, ${isMuted ? 'rgb(255 255 255 / 0.35)' : 'var(--color-accent)'} calc(5px + ${vol} * (100% - 10px)), rgb(255 255 255 / 0.2) 0)`,
                  }}
                />
                <span
                  className={`w-9 text-xs text-right tabular-nums ${isMuted ? 'text-white/30' : 'text-white/70'}`}
                >
                  {Math.round(vol * 100)}%
                </span>
              </div>
            </div>
          </Fragment>
        );
      })}
    </>
  );
}
