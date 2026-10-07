import type { Codec } from '../Models/types';

let hevcPlaybackSupport: boolean | null = null;

export function supportsHevcPlayback(): boolean {
  if (hevcPlaybackSupport === null) {
    const probe = document.createElement('video');
    hevcPlaybackSupport =
      probe.canPlayType('video/mp4; codecs="hvc1.1.6.L93.B0"') !== '' ||
      probe.canPlayType('video/mp4; codecs="hev1.1.6.L93.B0"') !== '';
  }
  return hevcPlaybackSupport;
}

export function isHevcEncoder(codec: Codec | null | undefined): boolean {
  const id = codec?.internalEncoderId?.toLowerCase() ?? '';
  return id.includes('hevc') || id.includes('h265') || id.includes('265');
}

function isH264Encoder(codec: Codec): boolean {
  const id = codec.internalEncoderId.toLowerCase();
  return !id.includes('av1') && !isHevcEncoder(codec);
}

export function pickPreferredH264Codec(codecs: Codec[], encoder: 'gpu' | 'cpu'): Codec | null {
  const candidates = codecs
    .filter((codec) => (encoder === 'gpu' ? codec.isHardwareEncoder : !codec.isHardwareEncoder))
    .filter(isH264Encoder);

  const priorityOrder =
    encoder === 'gpu' ? ['jim_nvenc', 'h264_texture_amf', 'ffmpeg_vaapi_tex'] : ['obs_x264'];

  for (const id of priorityOrder) {
    const match = candidates.find((codec) => codec.internalEncoderId.toLowerCase() === id);
    if (match) return match;
  }

  return candidates[0] ?? null;
}
