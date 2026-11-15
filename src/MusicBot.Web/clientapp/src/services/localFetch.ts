export function localFetch(input: RequestInfo | URL, init?: RequestInit) {
    const headers = new Headers(init?.headers); headers.set("X-Musick-Request", "1");
    return window.fetch(input, { ...init, headers });
}
