const observers = new WeakMap();

export function observe(element) {
    if (!element || observers.has(element)) return;
    const text = element.querySelector(".decision-preview-text");
    if (!text) return;
    let frame;
    const measure = () => {
        cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => {
            element.classList.toggle("is-truncated", text.scrollHeight > text.clientHeight + 1);
            element.classList.add("is-measured");
        });
    };
    const resize = new ResizeObserver(measure);
    const mutation = new MutationObserver(measure);
    resize.observe(text);
    mutation.observe(text, { childList: true, subtree: true, characterData: true });
    observers.set(element, () => { resize.disconnect(); mutation.disconnect(); cancelAnimationFrame(frame); });
    measure();
}

export function unobserve(element) {
    observers.get(element)?.();
    observers.delete(element);
}
