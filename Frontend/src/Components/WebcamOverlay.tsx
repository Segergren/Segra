import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { WebcamLayer } from '../Models/types';
import { useWebcamHidden } from './WebcamToggle';

interface WebcamOverlayProps {
  videoRef: React.RefObject<HTMLVideoElement | null>;
  layer: WebcamLayer;
  // Same zoom/pan transform as the main video, so the webcam stays on the same spot
  transform: string;
  scale: number;
  onChange: (x: number, y: number, size: number) => void;
  // Kept mounted while hidden so showing and hiding can animate
  videoId: string;
  onDraggingChange: (dragging: boolean) => void;
}

// Drift beyond this is corrected by seeking the webcam
const MAX_DRIFT_SECONDS = 0.15;

interface Box {
  left: number;
  top: number;
  width: number;
  height: number;
  videoWidth: number;
  videoHeight: number;
  areaLeft: number;
  areaTop: number;
  // Video pixels per displayed pixel, for the distance labels
  pixelScale: number;
}

// The 16:9 box inside the letterboxed video, placed like the recorder and clip export place it
function computeBox(video: HTMLVideoElement, x: number, y: number, size: number): Box | null {
  const vw = video.videoWidth;
  const vh = video.videoHeight;
  const cw = video.clientWidth;
  const ch = video.clientHeight;
  if (!vw || !vh || !cw || !ch) return null;

  const fit = Math.min(cw / vw, ch / vh);
  const videoWidth = vw * fit;
  const videoHeight = vh * fit;
  // Even video pixels for both sides, rounded the same way as clip export
  const even = (value: number) => Math.max(2, Math.round(value / 2) * 2);
  const naturalWidth = even((vw * Math.min(50, Math.max(5, size))) / 100);
  const width = naturalWidth * fit;
  const height = even(Math.min((naturalWidth * 9) / 16, vh)) * fit;
  const left =
    (cw - videoWidth) / 2 + Math.min(Math.max((videoWidth * x) / 100, 0), videoWidth - width);
  const top =
    (ch - videoHeight) / 2 + Math.min(Math.max((videoHeight * y) / 100, 0), videoHeight - height);
  const areaLeft = (cw - videoWidth) / 2;
  const areaTop = (ch - videoHeight) / 2;
  return {
    left,
    top,
    width,
    height,
    videoWidth,
    videoHeight,
    areaLeft,
    areaTop,
    pixelScale: vw / videoWidth,
  };
}

export default function WebcamOverlay({
  videoRef,
  layer,
  transform,
  scale,
  onChange,
  videoId,
  onDraggingChange,
}: WebcamOverlayProps) {
  const visible = !useWebcamHidden(videoId, layer.hidden);
  const camRef = useRef<HTMLVideoElement>(null);
  const [position, setPosition] = useState({ x: layer.x, y: layer.y, size: layer.size });
  const [box, setBox] = useState<Box | null>(null);
  const [inRange, setInRange] = useState(true);
  const [dragging, setDragging] = useState(false);
  const dragRef = useRef<{
    hx: -1 | 0 | 1;
    hy: -1 | 0 | 1;
    resize: boolean;
    pointerX: number;
    pointerY: number;
    x: number;
    y: number;
    size: number;
    moved: boolean;
  } | null>(null);

  // Unmounting mid-drag must not leave the player controls hidden
  useEffect(() => () => onDraggingChange(false), [onDraggingChange]);

  useEffect(
    () => setPosition({ x: layer.x, y: layer.y, size: layer.size }),
    [layer.x, layer.y, layer.size],
  );

  useLayoutEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    const update = () => setBox(computeBox(video, position.x, position.y, position.size));
    update();
    const observer = new ResizeObserver(update);
    observer.observe(video);
    video.addEventListener('loadedmetadata', update);
    return () => {
      observer.disconnect();
      video.removeEventListener('loadedmetadata', update);
    };
  }, [videoRef, position.x, position.y, position.size]);

  // Follows the main video: play/pause, seeks and playback rate
  useEffect(() => {
    const video = videoRef.current;
    const cam = camRef.current;
    if (!video || !cam) return;

    const sync = (forceSeek: boolean) => {
      const target = video.currentTime - layer.offset;
      const visible = target >= 0 && (!cam.duration || target <= cam.duration);
      setInRange(visible);
      if (!visible) {
        if (!cam.paused) cam.pause();
        return;
      }
      if (cam.playbackRate !== video.playbackRate) cam.playbackRate = video.playbackRate;
      // A seek still in flight would only be restarted, so drift waits for it
      if (forceSeek || (!cam.seeking && Math.abs(cam.currentTime - target) > MAX_DRIFT_SECONDS)) {
        cam.currentTime = target;
      }
      if (video.paused || video.seeking) {
        if (!cam.paused) cam.pause();
      } else if (cam.paused) {
        cam.play().catch(() => {});
      }
    };

    const onSeek = () => sync(true);
    const onState = () => sync(false);
    const onCamReady = () => sync(true);

    video.addEventListener('play', onState);
    video.addEventListener('pause', onSeek);
    video.addEventListener('seeking', onSeek);
    video.addEventListener('seeked', onSeek);
    video.addEventListener('ratechange', onState);
    video.addEventListener('timeupdate', onState);
    cam.addEventListener('loadedmetadata', onCamReady);
    sync(true);

    return () => {
      video.removeEventListener('play', onState);
      video.removeEventListener('pause', onSeek);
      video.removeEventListener('seeking', onSeek);
      video.removeEventListener('seeked', onSeek);
      video.removeEventListener('ratechange', onState);
      video.removeEventListener('timeupdate', onState);
      cam.removeEventListener('loadedmetadata', onCamReady);
      cam.pause();
    };
  }, [videoRef, layer.offset, layer.filePath]);

  // Resize handles on every corner and edge; hx/hy say which side moves (-1 left/top, 1 right/bottom)
  const handles: { hx: -1 | 0 | 1; hy: -1 | 0 | 1 }[] = [
    { hx: -1, hy: -1 },
    { hx: 0, hy: -1 },
    { hx: 1, hy: -1 },
    { hx: -1, hy: 0 },
    { hx: 1, hy: 0 },
    { hx: -1, hy: 1 },
    { hx: 0, hy: 1 },
    { hx: 1, hy: 1 },
  ];

  const startGesture = (
    e: React.PointerEvent<HTMLElement>,
    handle: { hx: -1 | 0 | 1; hy: -1 | 0 | 1 } | null,
  ) => {
    if (e.button !== 0) return;
    e.stopPropagation();
    e.currentTarget.setPointerCapture(e.pointerId);
    setDragging(true);
    onDraggingChange(true);
    dragRef.current = {
      hx: handle?.hx ?? 0,
      hy: handle?.hy ?? 0,
      resize: handle !== null,
      pointerX: e.clientX,
      pointerY: e.clientY,
      x: position.x,
      y: position.y,
      size: position.size,
      moved: false,
    };
  };

  const onHandlePointerDown = (e: React.PointerEvent<HTMLElement>) => {
    const { hx, hy } = e.currentTarget.dataset;
    startGesture(e, { hx: Number(hx) as -1 | 0 | 1, hy: Number(hy) as -1 | 0 | 1 });
  };

  const onPointerMove = (e: React.PointerEvent<HTMLElement>) => {
    const drag = dragRef.current;
    if (!drag || !box) return;
    e.stopPropagation();
    const dx = (e.clientX - drag.pointerX) / (scale || 1);
    const dy = (e.clientY - drag.pointerY) / (scale || 1);
    if (Math.abs(dx) + Math.abs(dy) > 2) drag.moved = true;
    const vw = box.videoWidth;
    const vh = box.videoHeight;
    // Whole steps of 2 video pixels, in displayed pixels
    const step = 2 / box.pixelScale;
    const even = (value: number) => Math.round(value / step) * step;

    if (!drag.resize) {
      const maxX = 100 - (box.width / vw) * 100;
      const maxY = 100 - (box.height / vh) * 100;
      // Snap to the video's edges within 0.5%
      const snap = (value: number, max: number) => {
        const clamped = Math.min(Math.max(value, 0), max);
        return clamped < 0.5 ? 0 : clamped > max - 0.5 ? max : clamped;
      };
      const x = Math.min(
        (even((snap(drag.x + (dx / vw) * 100, maxX) / 100) * vw) / vw) * 100,
        maxX,
      );
      const y = Math.min(
        (even((snap(drag.y + (dy / vh) * 100, maxY) / 100) * vh) / vh) * 100,
        maxY,
      );
      setPosition({ x, y, size: drag.size });
      return;
    }

    // Resize keeping 16:9: the opposite side (or the center, for an axis without a handle) stays put
    const { hx, hy } = drag;
    const left0 = (drag.x / 100) * vw;
    const top0 = (drag.y / 100) * vh;
    const w0 = (drag.size / 100) * vw;
    const h0 = (w0 * 9) / 16;
    const candidates: number[] = [];
    if (hx !== 0) candidates.push(w0 + hx * dx);
    if (hy !== 0) candidates.push(((h0 + hy * dy) * 16) / 9);
    const wanted = Math.max(...candidates);

    const roomX =
      hx === 1
        ? vw - left0
        : hx === -1
          ? left0 + w0
          : 2 * Math.min(left0 + w0 / 2, vw - left0 - w0 / 2);
    const roomY =
      hy === 1
        ? vh - top0
        : hy === -1
          ? top0 + h0
          : 2 * Math.min(top0 + h0 / 2, vh - top0 - h0 / 2);
    const maxWidth = Math.min(roomX, (roomY * 16) / 9, vw * 0.5);
    const clamped = Math.min(Math.max(wanted, vw * 0.05), maxWidth);
    let width = even(clamped);
    if (width > maxWidth) width -= step;
    const height = (width * 9) / 16;
    const left = hx === 1 ? left0 : hx === -1 ? left0 + w0 - width : left0 + (w0 - width) / 2;
    const top = hy === 1 ? top0 : hy === -1 ? top0 + h0 - height : top0 + (h0 - height) / 2;
    setPosition({
      x: (Math.max(0, even(left)) / vw) * 100,
      y: (Math.max(0, even(top)) / vh) * 100,
      size: (width / vw) * 100,
    });
  };

  const onPointerUp = (e: React.PointerEvent<HTMLElement>) => {
    const drag = dragRef.current;
    if (!drag) return;
    e.stopPropagation();
    dragRef.current = null;
    setDragging(false);
    onDraggingChange(false);
    if (drag.moved) onChange(position.x, position.y, position.size);
  };

  const src = `http://localhost:2222/api/content?input=${encodeURIComponent(layer.filePath)}`;

  return (
    <div
      className="absolute inset-0 pointer-events-none"
      style={{ transform, transformOrigin: '0 0' }}
    >
      <video
        ref={camRef}
        src={src}
        crossOrigin="anonymous"
        muted
        playsInline
        preload="auto"
        aria-label="Webcam, drag to move"
        className={`absolute cursor-move touch-none object-cover will-change-[opacity,scale] ${
          visible
            ? 'pointer-events-auto opacity-100 scale-100'
            : 'pointer-events-none opacity-0 scale-95'
        }`}
        style={{
          left: box?.left ?? 0,
          top: box?.top ?? 0,
          width: box?.width ?? 0,
          height: box?.height ?? 0,
          // Radius 6% of the height, same as clip export
          borderRadius: (box?.height ?? 0) * 0.06,
          visibility: box && inRange ? 'visible' : 'hidden',
          // Tailwind's scale-* sets the CSS scale property, not transform
          transition: visible
            ? 'opacity 150ms ease-out, scale 150ms ease-out'
            : 'opacity 150ms ease-in, scale 150ms ease-in',
        }}
        onPointerDown={(e) => startGesture(e, null)}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        onClick={(e) => e.stopPropagation()}
        onDoubleClick={(e) => e.stopPropagation()}
      />
      {/* Kept while dragging so the captured handle still gets its pointerup */}
      {box &&
        ((inRange && visible) || dragging) &&
        handles.map(({ hx, hy }) => {
          const grip = 12;
          const isCorner = hx !== 0 && hy !== 0;
          return (
            <div
              key={`${hx},${hy}`}
              aria-label="Resize webcam"
              className="absolute pointer-events-auto touch-none"
              style={{
                left:
                  box.left + (hx === -1 ? -grip / 2 : hx === 1 ? box.width - grip / 2 : grip / 2),
                top:
                  box.top + (hy === -1 ? -grip / 2 : hy === 1 ? box.height - grip / 2 : grip / 2),
                width: hx === 0 ? box.width - grip : grip,
                height: hy === 0 ? box.height - grip : grip,
                cursor: isCorner
                  ? hx === hy
                    ? 'nwse-resize'
                    : 'nesw-resize'
                  : hx !== 0
                    ? 'ew-resize'
                    : 'ns-resize',
              }}
              data-hx={hx}
              data-hy={hy}
              onPointerDown={onHandlePointerDown}
              onPointerMove={onPointerMove}
              onPointerUp={onPointerUp}
              onPointerCancel={onPointerUp}
              onClick={(e) => e.stopPropagation()}
              onDoubleClick={(e) => e.stopPropagation()}
            />
          );
        })}
      {box && inRange && visible && dragging && <DistanceGuides box={box} />}
    </div>
  );
}

// Lines from each side of the webcam to the video edge, labeled in video pixels
function DistanceGuides({ box }: { box: Box }) {
  const right = box.areaLeft + box.videoWidth;
  const bottom = box.areaTop + box.videoHeight;
  const midX = box.left + box.width / 2;
  const midY = box.top + box.height / 2;
  const gaps = [
    { key: 'left', from: box.areaLeft, to: box.left, horizontal: true, at: midY },
    { key: 'right', from: box.left + box.width, to: right, horizontal: true, at: midY },
    { key: 'top', from: box.areaTop, to: box.top, horizontal: false, at: midX },
    { key: 'bottom', from: box.top + box.height, to: bottom, horizontal: false, at: midX },
  ];

  return (
    <>
      {gaps.map(({ key, from, to, horizontal, at }) => {
        const length = Math.max(0, to - from);
        const label = `${Math.round(length * box.pixelScale)} px`;
        const center = from + length / 2;
        return (
          <div key={key} className="pointer-events-none">
            {length > 0 && (
              <div
                className="absolute border-primary border-dashed"
                style={
                  horizontal
                    ? { left: from, top: at, width: length, borderTopWidth: 1 }
                    : { left: at, top: from, height: length, borderLeftWidth: 1 }
                }
              />
            )}
            <span
              className="absolute -translate-x-1/2 -translate-y-1/2 rounded bg-base-300/80 px-1 text-[11px] leading-4 tabular-nums text-base-content whitespace-nowrap"
              style={horizontal ? { left: center, top: at } : { left: at, top: center }}
            >
              {label}
            </span>
          </div>
        );
      })}
    </>
  );
}
