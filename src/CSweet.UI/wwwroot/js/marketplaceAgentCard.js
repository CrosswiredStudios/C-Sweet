// Presentation-only interactions: hiring remains owned by the Blazor profile action.
export function attach(card) {
    const trigger = card.querySelector('.agent-brand-front');
    const panel = card.querySelector('.agent-brand-details');
    const close = card.querySelector('.agent-brand-close');
    const controller = new AbortController();
    const options = { signal: controller.signal };
    let pinned = false;
    let hovering = false;
    let restoringFocus = false;

    function show(open) {
        card.dataset.open = String(open);
        trigger.setAttribute('aria-expanded', String(open));
        panel.setAttribute('aria-hidden', String(!open));
        panel.inert = !open;
    }
    function dismiss(restoreFocus = false) {
        pinned = false;
        if (restoreFocus) {
            restoringFocus = true;
            trigger.focus({ preventScroll: true });
            restoringFocus = false;
        }
        show(false);
    }
    card.addEventListener('pointerenter', event => {
        if (event.pointerType === 'mouse' && matchMedia('(hover: hover)').matches) {
            hovering = true;
            show(true);
        }
    }, options);
    card.addEventListener('pointerleave', () => {
        hovering = false;
        if (!pinned && !card.contains(document.activeElement)) show(false);
    }, options);
    trigger.addEventListener('click', () => { pinned = true; show(true); }, options);
    trigger.addEventListener('focus', () => {
        if (!restoringFocus && trigger.matches(':focus-visible')) show(true);
    }, options);
    card.addEventListener('focusout', event => {
        if (!card.contains(event.relatedTarget)) {
            pinned = false;
            if (!hovering) show(false);
        }
    }, options);
    card.addEventListener('keydown', event => {
        if (event.key === 'Escape' && card.dataset.open === 'true') {
            event.preventDefault();
            event.stopPropagation();
            dismiss(true);
        }
    }, options);
    close.addEventListener('click', () => dismiss(true), options);
    document.addEventListener('pointerdown', event => {
        if (!card.contains(event.target)) dismiss();
    }, options);
    show(false);
    return { dispose() { controller.abort(); } };
}
