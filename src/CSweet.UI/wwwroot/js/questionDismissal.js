export async function dismiss(element) {
    if (!element?.isConnected || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

    // The negative margin also closes the parent grid's gap before Blazor removes the row.
    const parent = element.parentElement;
    const gap = Number.parseFloat(getComputedStyle(parent).rowGap) || 0;
    const height = element.getBoundingClientRect().height;
    element.style.pointerEvents = 'none';
    element.inert = true;
    element.style.overflow = 'hidden';
    try {
        await element.animate([{ opacity: 1 }, { opacity: 0 }],
            { duration: 150, easing: 'ease-out', fill: 'forwards' }).finished;
        if (!element.isConnected) return;
        await element.animate([
            { height: `${height}px`, marginBottom: '0px' },
            { height: '0px', marginBottom: `${-gap}px` }
        ], { duration: 180, easing: 'ease-in-out', fill: 'forwards' }).finished;
    } catch {
        // Removal or navigation can cancel an animation; the answer has already succeeded.
    }
}
