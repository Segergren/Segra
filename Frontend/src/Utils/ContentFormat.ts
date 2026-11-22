import i18n from '../i18n';
import { BookmarkType, Content } from '../Models/types';

const DAY_MS = 24 * 60 * 60 * 1000;

// Until 05:00 the night still counts as the evening before; after that, days follow the calendar
const NIGHT_END_HOUR = 5;

// The day a moment belongs to, as local midnight, when days start at startHour. Built from
// wall-clock fields so the night the clocks change still splits at that hour.
const startOfDay = (d: Date, startHour: number) => {
  const day = new Date(d.getFullYear(), d.getMonth(), d.getDate(), d.getHours() - startHour);
  return new Date(day.getFullYear(), day.getMonth(), day.getDate());
};

// Whole days between the date and today; 0 is today
const daysAgo = (date: Date, now: Date) => {
  const startHour = now.getHours() < NIGHT_END_HOUR ? NIGHT_END_HOUR : 0;
  return Math.round(
    (startOfDay(now, startHour).getTime() - startOfDay(date, startHour).getTime()) / DAY_MS,
  );
};

// Date formats follow the active UI language, so a Chinese UI keeps Chinese date order even
// on a machine whose OS locale is something else.
const formatterCache = new Map<string, Intl.DateTimeFormat>();
const localeFormatter = (options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat => {
  const locale = i18n.language || 'en';
  const key = `${locale}:${JSON.stringify(options)}`;
  let formatter = formatterCache.get(key);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat(locale, options);
    formatterCache.set(key, formatter);
  }
  return formatter;
};

// "Yesterday, 22:55", "Sat, 21:04" or "14 Nov"
export function formatCreatedAt(createdAt: string, now = new Date()): string {
  const date = new Date(createdAt);
  if (isNaN(date.getTime())) return '';
  const time = localeFormatter({ timeStyle: 'short' }).format(date);
  const days = daysAgo(date, now);
  if (days <= 0) return i18n.t('relative.today', { time });
  if (days === 1) return i18n.t('relative.yesterday', { time });
  if (days < 7)
    return i18n.t('relative.weekdayTime', {
      weekday: localeFormatter({ weekday: 'short' }).format(date),
      time,
    });
  return date.getFullYear() === now.getFullYear()
    ? localeFormatter({ day: 'numeric', month: 'short' }).format(date)
    : localeFormatter({ day: 'numeric', month: 'short', year: 'numeric' }).format(date);
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
  if (seconds < 60) return i18n.t('duration.seconds', { count: seconds });
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  const minutesText = i18n.t('duration.minutes', { count: minutes });
  if (rest === 0) return minutesText;
  const secondsText = i18n.t('duration.seconds', { count: rest });
  return i18n.t('duration.combined', { minutes: minutesText, seconds: secondsText });
}
