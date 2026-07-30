const states = new WeakMap();
const minimumScale = 0.35;
const maximumScale = 2.25;
const zoomStep = 1.2;

function clamp(value, minimum, maximum) {
    return Math.min(maximum, Math.max(minimum, value));
}

function apply(state, notify = false) {
    state.content.style.transform =
        `translate3d(${state.x}px, ${state.y}px, 0) scale(${state.scale})`;

    if (notify) {
        state.dotNet.invokeMethodAsync("UpdateZoom", state.scale).catch(() => {});
    }
}

function graphSize(state) {
    const width = Number.parseFloat(state.content.getAttribute("width")) || 720;
    const height = Number.parseFloat(state.content.getAttribute("height")) || 360;
    return { width, height };
}

function zoomAt(state, requestedScale, clientX, clientY) {
    const nextScale = clamp(requestedScale, minimumScale, maximumScale);
    if (Math.abs(nextScale - state.scale) < 0.001) {
        return;
    }

    const bounds = state.viewport.getBoundingClientRect();
    const pointX = clientX - bounds.left;
    const pointY = clientY - bounds.top;
    const ratio = nextScale / state.scale;
    state.x = pointX - ((pointX - state.x) * ratio);
    state.y = pointY - ((pointY - state.y) * ratio);
    state.scale = nextScale;
    state.interacted = true;
    apply(state, true);
}

function fitState(state) {
    const padding = 32;
    const graph = graphSize(state);
    const availableWidth = Math.max(1, state.viewport.clientWidth - (padding * 2));
    const availableHeight = Math.max(1, state.viewport.clientHeight - (padding * 2));
    state.scale = clamp(
        Math.min(1, availableWidth / graph.width, availableHeight / graph.height),
        minimumScale,
        maximumScale);
    state.x = (state.viewport.clientWidth - (graph.width * state.scale)) / 2;
    state.y = (state.viewport.clientHeight - (graph.height * state.scale)) / 2;
    state.interacted = false;
    apply(state, true);
}

export function initialize(viewport, content, dotNet) {
    dispose(viewport);

    const state = {
        viewport,
        content,
        dotNet,
        x: 0,
        y: 0,
        scale: 1,
        pointerId: null,
        lastClientX: 0,
        lastClientY: 0,
        interacted: false
    };

    state.pointerDown = event => {
        if (event.button !== 0 || event.target.closest("button")) {
            return;
        }

        state.pointerId = event.pointerId;
        state.lastClientX = event.clientX;
        state.lastClientY = event.clientY;
        viewport.setPointerCapture(event.pointerId);
        viewport.classList.add("is-dragging");
        event.preventDefault();
    };

    state.pointerMove = event => {
        if (event.pointerId !== state.pointerId) {
            return;
        }

        state.x += event.clientX - state.lastClientX;
        state.y += event.clientY - state.lastClientY;
        state.lastClientX = event.clientX;
        state.lastClientY = event.clientY;
        state.interacted = true;
        apply(state);
    };

    state.pointerUp = event => {
        if (event.pointerId !== state.pointerId) {
            return;
        }

        if (viewport.hasPointerCapture(event.pointerId)) {
            viewport.releasePointerCapture(event.pointerId);
        }
        state.pointerId = null;
        viewport.classList.remove("is-dragging");
    };

    state.wheel = event => {
        event.preventDefault();
        const factor = Math.exp(-event.deltaY * 0.0015);
        zoomAt(state, state.scale * factor, event.clientX, event.clientY);
    };

    state.keyDown = event => {
        const amount = event.shiftKey ? 80 : 40;
        if (event.key === "ArrowLeft") state.x += amount;
        else if (event.key === "ArrowRight") state.x -= amount;
        else if (event.key === "ArrowUp") state.y += amount;
        else if (event.key === "ArrowDown") state.y -= amount;
        else if (event.key === "+" || event.key === "=") {
            zoomAt(
                state,
                state.scale * zoomStep,
                viewport.getBoundingClientRect().left + (viewport.clientWidth / 2),
                viewport.getBoundingClientRect().top + (viewport.clientHeight / 2));
            event.preventDefault();
            return;
        } else if (event.key === "-" || event.key === "_") {
            zoomAt(
                state,
                state.scale / zoomStep,
                viewport.getBoundingClientRect().left + (viewport.clientWidth / 2),
                viewport.getBoundingClientRect().top + (viewport.clientHeight / 2));
            event.preventDefault();
            return;
        } else if (event.key === "0") {
            fitState(state);
            event.preventDefault();
            return;
        } else {
            return;
        }

        state.interacted = true;
        apply(state);
        event.preventDefault();
    };

    state.resizeObserver = new ResizeObserver(() => {
        if (!state.interacted) {
            fitState(state);
        }
    });

    viewport.addEventListener("pointerdown", state.pointerDown);
    viewport.addEventListener("pointermove", state.pointerMove);
    viewport.addEventListener("pointerup", state.pointerUp);
    viewport.addEventListener("pointercancel", state.pointerUp);
    viewport.addEventListener("wheel", state.wheel, { passive: false });
    viewport.addEventListener("keydown", state.keyDown);
    state.resizeObserver.observe(viewport);
    states.set(viewport, state);

    requestAnimationFrame(() => fitState(state));
}

export function zoomIn(viewport) {
    const state = states.get(viewport);
    if (!state) return;
    const bounds = viewport.getBoundingClientRect();
    zoomAt(
        state,
        state.scale * zoomStep,
        bounds.left + (bounds.width / 2),
        bounds.top + (bounds.height / 2));
}

export function zoomOut(viewport) {
    const state = states.get(viewport);
    if (!state) return;
    const bounds = viewport.getBoundingClientRect();
    zoomAt(
        state,
        state.scale / zoomStep,
        bounds.left + (bounds.width / 2),
        bounds.top + (bounds.height / 2));
}

export function fit(viewport) {
    const state = states.get(viewport);
    if (state) fitState(state);
}

export function dispose(viewport) {
    const state = states.get(viewport);
    if (!state) return;

    viewport.removeEventListener("pointerdown", state.pointerDown);
    viewport.removeEventListener("pointermove", state.pointerMove);
    viewport.removeEventListener("pointerup", state.pointerUp);
    viewport.removeEventListener("pointercancel", state.pointerUp);
    viewport.removeEventListener("wheel", state.wheel);
    viewport.removeEventListener("keydown", state.keyDown);
    state.resizeObserver.disconnect();
    states.delete(viewport);
}
