// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

// A window that wakes up (it was hidden, and the browser froze it) after Blazor gave up retrying
// tries again at once (#99). While Blazor is still retrying it does this by itself.
document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

// While the modal is open, the page asks the app whether it is still there (#99). A page can't
// restart the app, so once it has been gone for a while the modal says so instead of counting
// down to a rejoin that can't happen - and reloads by itself when the app is back (a restart
// to update, or the player opening it again). The wait keeps a restart from being called a stop.
const StoppedAfterMs = 15000;
const ProbeEveryMs = 5000;
let probeTimer = null;
let unreachableSince = null;

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        reconnectModal.showModal();
        watchServer();
    } else if (event.detail.state === "hide") {
        stopWatchingServer();
        reconnectModal.close();
    } else if (event.detail.state === "retrying") {
        watchServer();
    } else if (event.detail.state === "failed") {
        watchServer();
        retryIfServerIsThere();
    } else if (event.detail.state === "rejected") {
        location.reload();
    }
}

async function retry() {
    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                reconnectModal.close();
            }
        }
    } catch (err) {
        // The server is currently unavailable: the probe keeps watching for it.
        watchServer();
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible" && reconnectModal.classList.contains("components-reconnect-failed") && !isStopped()) {
        await retry();
    }
}

async function retryIfServerIsThere() {
    if (await serverAnswers()) {
        await retry();
    }
}

function watchServer() {
    if (probeTimer !== null) return;
    unreachableSince = null;
    probe();
    probeTimer = setInterval(probe, ProbeEveryMs);
}

function stopWatchingServer() {
    if (probeTimer !== null) clearInterval(probeTimer);
    probeTimer = null;
    unreachableSince = null;
    reconnectModal.classList.remove("components-reconnect-stopped");
}

async function probe() {
    if (await serverAnswers()) {
        unreachableSince = null;
        // The app is back after being gone: its old circuit died with it, so start afresh.
        if (isStopped()) location.reload();
        // Blazor gave up retrying while the app was there all along: try once more.
        else if (reconnectModal.classList.contains("components-reconnect-failed")) await retry();
        return;
    }

    unreachableSince ??= Date.now();
    if (Date.now() - unreachableSince >= StoppedAfterMs) {
        reconnectModal.classList.add("components-reconnect-stopped");
        if (!reconnectModal.open) reconnectModal.showModal();
    }
}

async function serverAnswers() {
    try {
        const response = await fetch("instance", { cache: "no-store", signal: AbortSignal.timeout(2000) });
        return response.ok;
    } catch {
        return false;
    }
}

function isStopped() {
    return reconnectModal.classList.contains("components-reconnect-stopped");
}
