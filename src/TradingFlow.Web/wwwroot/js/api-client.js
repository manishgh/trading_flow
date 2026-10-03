/* Shared authenticated API transport. Forms retain Razor's anti-forgery fields. */
window.tradingFlowFetch = async function (input, init = {}) {
    const url = new URL(input instanceof Request ? input.url : input, window.location.href);
    const method = (init.method ?? (input instanceof Request ? input.method : "GET")).toUpperCase();
    if (url.origin === window.location.origin && url.pathname.startsWith("/api/") && !["GET", "HEAD", "OPTIONS"].includes(method)) {
        const proof = await fetch("/api/auth/csrf", { credentials: "same-origin", cache: "no-store", signal: init.signal });
        if (!proof.ok) throw new Error("Unable to refresh session security. Sign in again.");
        const { requestToken } = await proof.json();
        const headers = new Headers(init.headers ?? (input instanceof Request ? input.headers : undefined));
        headers.set("X-CSRF-TOKEN", requestToken);
        init = { ...init, headers, credentials: "same-origin" };
    }
    return fetch(input, init);
};
