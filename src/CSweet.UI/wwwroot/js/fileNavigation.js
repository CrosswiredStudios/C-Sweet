// Lightweight URL updates for in-place file navigation in the source-control browser.
// pushState updates the address bar without triggering the Blazor router, so only the
// file preview re-renders. Back/forward are handled by the router's own popstate
// handling, which re-enters the page with the URL's query parameters.
export function pushState(url) {
    history.pushState(null, "", url);
}
