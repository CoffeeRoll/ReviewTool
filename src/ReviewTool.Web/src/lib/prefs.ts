import { useCallback, useSyncExternalStore } from 'react';

/**
 * Personal display preferences stored in the browser (shared by all reviews). Components using
 * usePref re-render when a preference changes anywhere in the page.
 */
const listeners = new Set<() => void>();
const PREFIX = 'reviewtool.';

export function getPref<T extends string>(key: string, fallback: T, allowed: readonly T[]): T {
  try {
    const v = localStorage.getItem(PREFIX + key) as T | null;
    return v && allowed.includes(v) ? v : fallback;
  } catch {
    return fallback;
  }
}

export function setPref(key: string, value: string) {
  try {
    localStorage.setItem(PREFIX + key, value);
  } catch {
    /* storage unavailable: the change still applies until reload */
    memory.set(key, value);
  }
  listeners.forEach((l) => l());
}

const memory = new Map<string, string>();

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function usePref<T extends string>(key: string, fallback: T, allowed: readonly T[]): [T, (value: T) => void] {
  const value = useSyncExternalStore(subscribe, () => (memory.get(key) as T | undefined) ?? getPref(key, fallback, allowed));
  const set = useCallback((v: T) => setPref(key, v), [key]);
  return [value, set];
}

/** How already-reviewed files and hunks are shown when you open a commit. */
export const REVIEWED_DISPLAY = ['show', 'collapse'] as const;
export type ReviewedDisplay = (typeof REVIEWED_DISPLAY)[number];
export const useReviewedDisplay = () => usePref<ReviewedDisplay>('reviewedDisplay', 'show', REVIEWED_DISPLAY);
