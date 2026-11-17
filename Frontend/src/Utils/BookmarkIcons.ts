import { createElement, forwardRef } from 'react';
import type { LucideIcon, LucideProps } from 'lucide-react';
import { Icon, Bookmark, HeartHandshake, Skull, Swords } from 'lucide-react';
import { crosshair2Dot, soccerBall } from '@lucide/lab';
import { BookmarkType } from '../Models/types';

export const Crosshair2Dot = forwardRef<SVGSVGElement, LucideProps>((props, ref) =>
  createElement(Icon, { ...props, ref, iconNode: crosshair2Dot }),
) as LucideIcon;

const SoccerBall = forwardRef<SVGSVGElement, LucideProps>((props, ref) =>
  createElement(Icon, { ...props, ref, iconNode: soccerBall }),
) as LucideIcon;

const DEFAULT_ICON_MAPPING: Record<BookmarkType, LucideIcon> = {
  Manual: Bookmark,
  Kill: Crosshair2Dot,
  Goal: SoccerBall,
  Assist: HeartHandshake,
  Death: Skull,
};

const GAME_ICON_OVERRIDES: Record<number, Partial<Record<BookmarkType, LucideIcon>>> = {
  115: { Kill: Swords }, // League of Legends
};

export function getIconMapping(igdbId?: number): Record<BookmarkType, LucideIcon> {
  if (igdbId && GAME_ICON_OVERRIDES[igdbId]) {
    return { ...DEFAULT_ICON_MAPPING, ...GAME_ICON_OVERRIDES[igdbId] };
  }
  return DEFAULT_ICON_MAPPING;
}
