import { AudioLines, Headphones, Mic } from 'lucide-react';
import type { AudioTrackType } from '../Models/types';

interface AudioTrackIconProps {
  type?: AudioTrackType;
  className?: string;
}

export default function AudioTrackIcon({ type, className = 'h-3.5 w-3.5' }: AudioTrackIconProps) {
  if (type === 'input') return <Mic className={className} aria-hidden="true" />;
  if (type === 'output') return <Headphones className={className} aria-hidden="true" />;
  return <AudioLines className={className} aria-hidden="true" />;
}
