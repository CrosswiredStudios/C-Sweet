export function scrollToColumn(container, columnId) {
    const column = Array.from(container?.children ?? []).find(element => element.dataset.column === columnId);
    if (!column) return;
    container.scrollTo({
        left: container.scrollLeft + column.getBoundingClientRect().left - container.getBoundingClientRect().left,
        behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth'
    });
}
