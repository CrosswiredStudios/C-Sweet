const observers = new WeakMap();
const previousFocus = new WeakMap();

export function rememberFocus(element) {
    previousFocus.set(element, document.activeElement);
}

export function restoreFocus(element) {
    const previous = previousFocus.get(element);
    if (previous?.isConnected) previous.focus();
    previousFocus.delete(element);
}

export function observeWidth(element, storageKey) {
    const saved = localStorage.getItem(storageKey);
    if (saved) element.style.width = saved;
    const observer = new ResizeObserver(entries => {
        const width = Math.round(entries[0].contentRect.width);
        if (width > 0) localStorage.setItem(storageKey, `${width}px`);
    });
    observer.observe(element);
    observers.set(element, observer);
}

export function stopObservingWidth(element) {
    const observer = observers.get(element);
    if (observer) observer.disconnect();
    observers.delete(element);
}
