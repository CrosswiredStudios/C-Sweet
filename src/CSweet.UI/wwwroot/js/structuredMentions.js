export function getCaret(inputId) {
    const input = document.getElementById(inputId);
    return input?.selectionStart ?? input?.value?.length ?? 0;
}

export function setCaret(inputId, position) {
    const input = document.getElementById(inputId);
    if (!input) return;
    input.focus();
    input.setSelectionRange(position, position);
}

export function setMenuOpen(inputId, isOpen) {
    const input = document.getElementById(inputId);
    if (!input) return;
    input.dataset.mentionMenuOpen = isOpen ? "true" : "false";
    if (input.dataset.mentionKeyboardAttached === "true") return;
    input.dataset.mentionKeyboardAttached = "true";
    input.addEventListener("keydown", event => {
        if (input.dataset.mentionMenuOpen !== "true") return;
        if (["Tab", "Enter", "ArrowUp", "ArrowDown", "Escape"].includes(event.key))
            event.preventDefault();
    });
}
