// Parses a numeric input value into an integer within [min, max].
// Empty or non-numeric input yields the fallback; a typed 0 is kept.
export function clampInt(value: string, min: number, max: number, fallback: number): number {
  const parsed = value.trim() === '' ? NaN : Number(value);
  const n = Number.isFinite(parsed) ? Math.round(parsed) : fallback;
  return Math.min(max, Math.max(min, n));
}
