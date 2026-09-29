const bindings = new WeakMap();

export function sync(root, reference, key, follow) {
    let state = bindings.get(root);
    if (!state) {
        state = { reference, key: null, feed: null, scrolling: false };
        state.outside = event => {
            if (!state.key) return;
            const panel = root.querySelector('[data-activity-popover]');
            const opener = event.target.closest?.('[data-current-activity-open]');
            if (panel && !panel.contains(event.target) && !(opener && root.contains(opener)))
                state.reference.invokeMethodAsync('CloseDetails').catch(() => {});
        };
        state.keyboard = event => {
            if (event.key === 'Escape' && state.key) {
                event.preventDefault();
                state.reference.invokeMethodAsync('CloseDetails').catch(() => {});
            }
        };
        document.addEventListener('pointerdown', state.outside);
        document.addEventListener('keydown', state.keyboard);
        bindings.set(root, state);
    }
    if (key !== state.key) {
        if (key) root.querySelector('[data-activity-close]')?.focus({ preventScroll: true });
        else [...root.querySelectorAll('[data-current-activity-open]')]
            .find(button => button.dataset.currentActivityOpen === state.key)?.focus({ preventScroll: true });
        state.key = key;
    }
    const feed = root.querySelector('[data-activity-feed]');
    if (feed !== state.feed) {
        state.feed?.removeEventListener('scroll', state.scroll);
        state.feed = feed;
        state.scroll = () => {
            if (!state.scrolling && feed.scrollHeight - feed.scrollTop - feed.clientHeight > 40)
                state.reference.invokeMethodAsync('StopFollowing').catch(() => {});
        };
        feed?.addEventListener('scroll', state.scroll, { passive: true });
    }
    if (follow && feed) {
        state.scrolling = true;
        feed.scrollTop = feed.scrollHeight;
        requestAnimationFrame(() => { state.scrolling = false; });
    }
}

export function dispose(root) {
    const state = bindings.get(root);
    if (!state) return;
    document.removeEventListener('pointerdown', state.outside);
    document.removeEventListener('keydown', state.keyboard);
    state.feed?.removeEventListener('scroll', state.scroll);
    bindings.delete(root);
}
