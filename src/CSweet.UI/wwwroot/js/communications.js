export function isDocumentVisible() {
    return document.visibilityState === "visible";
}

export function scrollToBottom(element) {
    if (!element) return;
    element.scrollTop = element.scrollHeight;
}

export function scrollHiringCarousel(element, direction) {
    if (!element) return;
    const card = element.querySelector(".hiring-suggestion-tile");
    const styles = window.getComputedStyle(element);
    const gap = Number.parseFloat(styles.columnGap || styles.gap) || 0;
    const distance = (card?.getBoundingClientRect().width || element.clientWidth * .8) + gap;
    const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    element.scrollBy({
        left: Math.sign(direction) * distance,
        behavior: reduceMotion ? "auto" : "smooth"
    });
}

export function getComposerCaret(inputId) {
    const input = document.getElementById(inputId);
    return input?.selectionStart ?? input?.value?.length ?? 0;
}

export function setComposerCaret(inputId, position) {
    const input = document.getElementById(inputId);
    if (!input) return;
    input.focus();
    input.setSelectionRange(position, position);
}

export function setMentionMenuOpen(inputId, isOpen) {
    const input = document.getElementById(inputId);
    if (!input) return;
    input.dataset.mentionMenuOpen = isOpen ? "true" : "false";
    if (input.dataset.mentionKeyboardAttached === "true") return;
    input.dataset.mentionKeyboardAttached = "true";
    input.addEventListener("keydown", event => {
        if (input.dataset.mentionMenuOpen !== "true") return;
        if (["Tab", "Enter", "ArrowUp", "ArrowDown", "Escape"].includes(event.key)) {
            event.preventDefault();
        }
    });
}
