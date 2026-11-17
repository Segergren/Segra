import { useState } from 'react';
import { FileUp, Gamepad2, Monitor } from 'lucide-react';

const ICON_CLASS = 'shrink-0 text-base-content/60';

interface GameIconProps {
  src: string | null;
  // Display recordings and imports without a game get their own symbol instead of a game icon
  kind?: 'game' | 'display' | 'import';
  size?: number;
}

export default function GameIcon({ src, kind = 'game', size = 20 }: GameIconProps) {
  const [failed, setFailed] = useState(false);
  const symbolSize = size - 2;
  if (kind === 'display') return <Monitor size={symbolSize} className={ICON_CLASS} />;
  if (kind === 'import') return <FileUp size={symbolSize} className={ICON_CLASS} />;
  if (!src || failed) return <Gamepad2 size={symbolSize} className={ICON_CLASS} />;
  return (
    <img
      src={src}
      alt=""
      width={size}
      height={size}
      className="rounded object-cover shrink-0"
      style={{ width: size, height: size }}
      draggable={false}
      onError={() => setFailed(true)}
    />
  );
}
