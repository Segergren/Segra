import { Cpu } from 'lucide-react';
import { GpuVendor } from '../Models/types';

// Logo paths from Simple Icons (CC0)
const vendors: Partial<Record<GpuVendor, { color: string; path: string; viewBox?: string }>> = {
  [GpuVendor.Nvidia]: {
    color: '#76B900',
    path: 'M8.948 8.798v-1.43a6.7 6.7 0 0 1 .424-.018c3.922-.124 6.493 3.374 6.493 3.374s-2.774 3.851-5.75 3.851c-.398 0-.787-.062-1.158-.185v-4.346c1.528.185 1.837.857 2.747 2.385l2.04-1.714s-1.492-1.952-4-1.952a6.016 6.016 0 0 0-.796.035m0-4.735v2.138l.424-.027c5.45-.185 9.01 4.47 9.01 4.47s-4.08 4.964-8.33 4.964c-.37 0-.733-.035-1.095-.097v1.325c.3.035.61.062.91.062 3.957 0 6.82-2.023 9.593-4.408.459.371 2.34 1.263 2.73 1.652-2.633 2.208-8.772 3.984-12.253 3.984-.335 0-.653-.018-.971-.053v1.864H24V4.063zm0 10.326v1.131c-3.657-.654-4.673-4.46-4.673-4.46s1.758-1.944 4.673-2.262v1.237H8.94c-1.528-.186-2.73 1.245-2.73 1.245s.68 2.412 2.739 3.11M2.456 10.9s2.164-3.197 6.5-3.533V6.201C4.153 6.59 0 10.653 0 10.653s2.35 6.802 8.948 7.42v-1.237c-4.84-.6-6.492-5.936-6.492-5.936z',
  },
  [GpuVendor.AMD]: {
    color: '#ED1C24',
    // Arrow mark only, the full logo includes the wordmark
    viewBox: '18 8.86 6.28 6.28',
    path: 'M18.324 9.137l1.559 1.56h2.556v2.557L24 14.814V9.137zM19.881 11.01l-1.604 1.603v2.25h2.246l1.604-1.607h-2.246z',
  },
  [GpuVendor.Intel]: {
    color: '#0071C5',
    path: 'M20.42 7.345v9.18h1.651v-9.18zM0 7.475v1.737h1.737V7.474zm9.78.352v6.053c0 .513.044.945.13 1.292.087.34.235.618.44.828.203.21.475.359.803.451.334.093.754.136 1.255.136h.216v-1.533c-.24 0-.445-.012-.593-.037a.672.672 0 0 1-.39-.173.693.693 0 0 1-.173-.377 4.002 4.002 0 0 1-.037-.606v-2.182h1.193v-1.416h-1.193V7.827zm-3.505 2.312c-.396 0-.76.08-1.082.241-.327.161-.6.384-.822.668l-.087.117v-.902H2.658v6.256h1.639v-3.214c.018-.588.16-1.02.433-1.299.29-.297.642-.445 1.044-.445.476 0 .841.149 1.082.433.235.284.359.686.359 1.2v3.324h1.663V12.97c.006-.89-.229-1.595-.686-2.09-.458-.495-1.1-.742-1.917-.742zm10.065.006a3.252 3.252 0 0 0-2.306.946c-.29.29-.525.637-.692 1.033a3.145 3.145 0 0 0-.254 1.273c0 .452.08.878.241 1.274.161.395.39.742.674 1.032.284.29.637.526 1.045.693.408.173.86.26 1.342.26 1.397 0 2.262-.637 2.782-1.23l-1.187-.904c-.248.297-.841.699-1.583.699-.464 0-.847-.105-1.138-.321a1.588 1.588 0 0 1-.593-.872l-.019-.056h4.915v-.587c0-.451-.08-.872-.235-1.267a3.393 3.393 0 0 0-.661-1.033 3.013 3.013 0 0 0-1.02-.692 3.345 3.345 0 0 0-1.311-.248zm-16.297.118v6.256h1.651v-6.256zm16.278 1.286c1.132 0 1.664.797 1.664 1.255l-3.32.006c0-.458.525-1.255 1.656-1.261z',
  },
};

export function getEncoderVendor(encoderId: string): GpuVendor | null {
  if (encoderId.includes('nvenc')) return GpuVendor.Nvidia;
  if (encoderId.includes('amf')) return GpuVendor.AMD;
  if (encoderId.includes('qsv')) return GpuVendor.Intel;
  return null;
}

export default function EncoderVendorIcon({
  encoderId,
  isHardware,
}: {
  encoderId: string;
  isHardware: boolean;
}) {
  const vendor = getEncoderVendor(encoderId);
  const icon = vendor && vendors[vendor];
  if (icon) {
    return (
      <svg
        viewBox={icon.viewBox ?? '0 0 24 24'}
        className="w-4 h-4 shrink-0"
        fill={icon.color}
        aria-hidden
      >
        <path d={icon.path} />
      </svg>
    );
  }
  if (!isHardware) return <Cpu size={16} className="shrink-0 opacity-70" aria-hidden />;
  return null;
}
