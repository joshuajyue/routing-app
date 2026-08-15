const handlers = new WeakMap();

export function attachComposer(elementId, dotNetReference) {
    const composer = document.getElementById(elementId);
    if (!composer || handlers.has(composer)) {
        return;
    }

    let sending = false;
    const handler = async event => {
        if (event.key !== "Enter" || event.shiftKey || event.isComposing) {
            return;
        }

        event.preventDefault();
        if (sending || composer.disabled || !composer.value.trim()) {
            return;
        }

        sending = true;
        try {
            await dotNetReference.invokeMethodAsync("SendPromptFromKeyboardAsync");
        } finally {
            sending = false;
        }
    };

    composer.addEventListener("keydown", handler);
    handlers.set(composer, handler);
}

export function detachComposer(elementId) {
    const composer = document.getElementById(elementId);
    const handler = composer && handlers.get(composer);
    if (!composer || !handler) {
        return;
    }

    composer.removeEventListener("keydown", handler);
    handlers.delete(composer);
}
