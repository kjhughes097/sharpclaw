function getLocale(): string | undefined {
    if (typeof navigator === 'undefined') return undefined;
    return navigator.languages?.[0] ?? navigator.language ?? undefined;
}

function parseDate(value: string | null | undefined): Date | null {
    if (!value) return null;
    const d = new Date(value);
    return Number.isNaN(d.getTime()) ? null : d;
}

export function formatDateTime(value: string | null | undefined, fallback = '—'): string {
    const d = parseDate(value);
    if (!d) return fallback;
    return d.toLocaleString(getLocale());
}

export function formatDate(value: string | null | undefined, fallback = '—'): string {
    const d = parseDate(value);
    if (!d) return fallback;
    return d.toLocaleDateString(getLocale());
}

export function formatTime(value: string | null | undefined, fallback = '—'): string {
    const d = parseDate(value);
    if (!d) return fallback;
    return d.toLocaleTimeString(getLocale());
}
