import { BookmarkType, Content } from '../Models/types';

const DAY_MS = 24 * 60 * 60 * 1000;

// A gaming night that runs past midnight still belongs to that evening, so days start at 05:00
const DAY_START_HOUR = 5;

// The day a moment belongs to, as local midnight. Built from wall-clock fields so the night
// the clocks change still splits at 05:00.
const gamingDay = (d: Date) =>
  new Date(d.getFullYear(), d.getMonth(), d.getDate(), d.getHours() - DAY_START_HOUR);
const startOfGamingDay = (d: Date) => {
  const day = gamingDay(d);
  return new Date(day.getFullYear(), day.getMonth(), day.getDate());
};

// Whole days between the date and today; 0 is today
const daysAgo = (date: Date, now: Date) =>
  Math.round((startOfGamingDay(now).getTime() - startOfGamingDay(date).getTime()) / DAY_MS);

// timeStyle keeps each locale's own form: 00:19 in 24-hour locales, 12:19 AM in en-US
export const timeFormat = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' });
const shortWeekdayFormat = new Intl.DateTimeFormat(undefined, { weekday: 'short' });
const dayMonthFormat = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short' });
const dayMonthYearFormat = new Intl.DateTimeFormat(undefined, {
  day: 'numeric',
  month: 'short',
  year: 'numeric',
});

// "Yesterday, 22:55", "Sat, 21:04" or "14 Nov"
export function formatCreatedAt(createdAt: string, now = new Date()): string {
  const date = new Date(createdAt);
  if (isNaN(date.getTime())) return '';
  const time = timeFormat.format(date);
  const days = daysAgo(date, now);
  if (days <= 0) return `Today, ${time}`;
  if (days === 1) return `Yesterday, ${time}`;
  if (days < 7) return `${shortWeekdayFormat.format(gamingDay(date))}, ${time}`;
  return date.getFullYear() === now.getFullYear()
    ? dayMonthFormat.format(date)
    : dayMonthYearFormat.format(date);
}

const sizeFormat = new Intl.NumberFormat(undefined, {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});
const largeSizeFormat = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });

// The backend's size string depends on the locale it was written in, so format from the KB count
export function formatFileSize(content: Pick<Content, 'fileSize' | 'fileSizeKb'>): string {
  const kb = content.fileSizeKb;
  if (!kb || kb <= 0) return content.fileSize;
  const mb = kb / 1024;
  if (mb < 1024) return `${(mb < 100 ? sizeFormat : largeSizeFormat).format(mb)} MB`;
  return `${sizeFormat.format(mb / 1024)} GB`;
}

export interface KillDeathCount {
  kills: number;
  deaths: number;
  goals: number;
  // A highlight only holds kills, so a death count of 0 there means nothing
  showDeaths: boolean;
}

// Match stats when the game reports them, otherwise the kill, death and goal bookmarks
export function getKillDeathCount(content: Content): KillDeathCount | null {
  const matches = content.matches ?? [];
  const reported = matches.filter((m) => m.kills != null && m.deaths != null);
  if (reported.length > 0 && reported.length === matches.length) {
    return {
      kills: reported.reduce((sum, m) => sum + (m.kills ?? 0), 0),
      deaths: reported.reduce((sum, m) => sum + (m.deaths ?? 0), 0),
      goals: 0,
      showDeaths: true,
    };
  }
  const bookmarks = content.bookmarks ?? [];
  const kills = bookmarks.filter((b) => b.type === BookmarkType.Kill).length;
  const deaths = bookmarks.filter((b) => b.type === BookmarkType.Death).length;
  const goals = bookmarks.filter((b) => b.type === BookmarkType.Goal).length;
  if (kills === 0 && deaths === 0 && goals === 0) return null;
  return { kills, deaths, goals, showDeaths: content.type !== 'Highlight' && kills + deaths > 0 };
}

// "45 seconds", "2 minutes" or "1 minute 30 seconds"
export function formatBufferLength(seconds: number): string {
  if (seconds < 60) return `${seconds} seconds`;
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  const minutesText = `${minutes} minute${minutes === 1 ? '' : 's'}`;
  return rest === 0 ? minutesText : `${minutesText} ${rest} seconds`;
}
