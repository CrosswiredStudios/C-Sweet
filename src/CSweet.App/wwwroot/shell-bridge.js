// Thin-shell bridge (TS-13 prototype). Active only inside the C-Sweet phone app, which injects `csweetShell`
// for this enterprise's origin and main frame only. API calls are posted to the native shell and answered by id.
(function () {
    if (!window.csweetShell) return;
    var next = 1;
    var pending = new Map();
    window.csweetShell.addEventListener('message', function (event) {
        var reply;
        try { reply = JSON.parse(event.data); } catch (e) { return; }
        var resolve = pending.get(reply.id);
        if (!resolve) return;
        pending.delete(reply.id);
        resolve({ status: reply.status, contentType: reply.contentType || null, body: reply.body || null });
    });
    window.csweetShellBridge = {
        request: function (method, path, contentType, body, operationKey) {
            return new Promise(function (resolve) {
                var id = next++;
                pending.set(id, resolve);
                window.csweetShell.postMessage(JSON.stringify({ id: id, method: method, path: path, contentType: contentType, body: body, operationKey: operationKey || null }));
            });
        }
    };
})();
