export async function copyText(text) {
    await navigator.clipboard.writeText(text);
}

export function downloadText(fileName, text) {
    const blob = new Blob([text], { type: "text/plain;charset=utf-8" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(link.href);
}

export function printText(title, text) {
    const popup = window.open("", "_blank", "noopener,noreferrer");
    if (!popup) return;
    popup.document.title = title;
    const heading = popup.document.createElement("h1");
    heading.textContent = title;
    const content = popup.document.createElement("pre");
    content.textContent = text;
    popup.document.body.append(heading, content);
    popup.print();
}

export function launchExternalUri(uri) {
    const link = document.createElement("a");
    link.href = uri;
    link.style.display = "none";
    document.body.appendChild(link);
    link.click();
    link.remove();
}
