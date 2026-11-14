// Pizza Circuit Lab — browser side.
//
// This script never decides to pause a circuit and offers no pause/resume controls.
//  * Hidden-tab auto-pause and its resume are done by the framework's AutoPause module.
//  * Administrative pauses arrive from the server (Circuit.RequestCircuitPauseAsync).
//  * A paused circuit has no connection, so the server cannot resume it. While paused, this
//    script asks the server whether an administrator has released the session and only then
//    calls Blazor.resumeCircuit().
// Everything it shows is what this tab observed locally; server-reported facts are rendered
// by the Blazor component.
(() => {
    'use strict';
    if (window.pizzaLab) return; // never register listeners twice

    const POLL_MS = 2000;
    const RETRY_MS = 5000;
    const TAB_RESUME_GRACE_MS = 3000;
    const LOG_KEY = 'pizzaLab.log';
    const RELOAD_KEY = 'pizzaLab.lastReload';
    const LOG_MAX = 10;

    const state = {
        link: 'starting',      // starting | connected | reconnecting | paused | failed
        pauseKind: null,       // 'admin' (server-initiated) | 'tab' (hidden-tab auto-pause)
        attempt: 0,
        hold: null,            // last server answer about an administrative hold
        holdAt: null,
        serverReachable: true,
        hiddenAt: null,
        visibleAt: Date.now(),
        busy: false,
        dotNetRef: null
    };
    let timer = null;
    let lastCircuitId = null;

    // ------------------------------------------------------------------ helpers

    const $ = id => document.getElementById(id);
    const clock = (date = new Date()) => date.toLocaleTimeString([], { hour12: false });
    const autoPauseSeconds = () => Number(document.body.dataset.autopauseSeconds) || 12;

    function serverIds() {
        const el = $('lab-server-state');
        return { sessionId: el?.dataset.sessionId || null, circuitId: el?.dataset.circuitId || null };
    }

    function readLog() {
        try { return JSON.parse(sessionStorage.getItem(LOG_KEY)) || []; } catch { return []; }
    }

    function note(message) {
        const entries = readLog();
        entries.unshift({ at: clock(), message });
        entries.length = Math.min(entries.length, LOG_MAX);
        try { sessionStorage.setItem(LOG_KEY, JSON.stringify(entries)); } catch { /* storage unavailable */ }
        render();
    }

    function linkText() {
        switch (state.link) {
            case 'connected': return 'connected';
            case 'reconnecting': return `connection lost · reconnecting${state.attempt ? ` (attempt ${state.attempt})` : ''}`;
            case 'failed': return 'server unreachable · retrying';
            case 'paused': return state.pauseKind === 'admin' ? 'paused · by administrator' : 'paused · tab was hidden';
            default: return 'starting';
        }
    }

    function render() {
        const set = (id, text) => { const el = $(id); if (el && el.textContent !== text) el.textContent = text; };
        set('bm-visibility', document.visibilityState);
        set('bm-link', linkText() + (state.serverReachable ? '' : ' · server not answering'));
        set('bm-hold', state.hold === null ? 'not asked (only checked while paused)'
            : `${state.hold ? 'yes' : 'no'} · server said so at ${state.holdAt}`);
        const monitor = $('browser-monitor');
        if (monitor) monitor.dataset.link = state.link;

        const log = $('bm-log');
        if (log) {
            log.replaceChildren(...readLog().map(entry => {
                const li = document.createElement('li');
                const time = document.createElement('time');
                time.textContent = entry.at;
                li.append(time, ' ', entry.message);
                return li;
            }));
        }

        // The browser knows it is offline at once; the open socket only times out about 30s later.
        const offlineUndetected = !navigator.onLine && state.link === 'connected';
        $('components-reconnect-modal')?.classList.toggle('lab-browser-offline', offlineUndetected);
        if (offlineUndetected) set('bm-link', 'connected · browser is offline, loss not detected yet');

        let title = 'Connection interrupted', text = 'Reconnecting to the same circuit…';
        if (offlineUndetected) {
            title = 'Network is offline';
            text = 'The browser reports no network. The circuit’s connection has not timed out yet; Blazor notices within about 30 seconds and starts reconnecting. Nothing is lost: the server keeps this circuit.';
        } else if (state.link === 'paused' && state.pauseKind === 'admin') {
            title = 'Paused by an administrator';
            text = 'The server persisted this page’s state and released its circuit. It resumes into a new circuit when an operator runs Resume in the Aspire Dashboard. There is no resume control here by design.';
        } else if (state.link === 'paused') {
            title = 'Paused while the tab was hidden';
            text = state.hold ? 'An administrator is holding this session. It resumes when an operator runs Resume in the Aspire Dashboard.'
                : 'Restoring your state into a new circuit…';
        } else if (state.link === 'failed') {
            title = 'Server unreachable';
            text = 'Still trying automatically. If pizza-web was stopped in Aspire, start it again.';
        }
        set('lab-overlay-title', title);
        set('lab-overlay-text', text);
    }

    // ------------------------------------------------------------------ recovery

    // Used only when the server no longer has anything to resume (process restarted, state expired).
    function reloadAsNewSession(reason) {
        let last = 0;
        try { last = Number(sessionStorage.getItem(RELOAD_KEY)) || 0; } catch { /* ignore */ }
        if (Date.now() - last < 15000) {
            note(`${reason}. Automatic reload already tried; reload the page to start a new session.`);
            stopTimer();
            return;
        }
        try { sessionStorage.setItem(RELOAD_KEY, String(Date.now())); } catch { /* ignore */ }
        note(`${reason}. Reloaded as a NEW session; the previous circuit state is gone.`);
        location.reload();
    }

    async function fetchDirective() {
        const { sessionId } = serverIds();
        if (!sessionId) return null;
        try {
            const response = await fetch(`/_lab/sessions/${encodeURIComponent(sessionId)}/directive`, { cache: 'no-store' });
            state.serverReachable = response.ok;
            return response.ok ? await response.json() : null;
        } catch {
            state.serverReachable = false; // stopped or restarting; keep asking quietly
            return null;
        }
    }

    async function resume(why) {
        note(`Resuming (${why})…`);
        let resumed;
        try {
            resumed = await Blazor.resumeCircuit();
        } catch (error) {
            note(`Resume attempt failed: ${error?.message ?? error}`);
            return;
        }
        if (!resumed && state.link === 'paused') reloadAsNewSession('The server could not restore the persisted state');
    }

    async function tick() {
        if (state.busy || document.visibilityState !== 'visible') return;
        state.busy = true;
        try {
            if (state.link === 'paused') {
                const directive = await fetchDirective();
                if (!directive || state.link !== 'paused') return;
                if (!directive.known) return reloadAsNewSession('The server no longer knows this session');
                state.hold = directive.adminHold;
                state.holdAt = clock();
                if (state.pauseKind === 'admin') {
                    if (directive.resumeRequested) await resume('administrator issued Resume');
                } else if (!directive.adminHold && Date.now() - state.visibleAt > TAB_RESUME_GRACE_MS) {
                    // The AutoPause module resumes on its own; this is the fallback if that attempt failed.
                    await resume('tab is visible again');
                }
            } else if (state.link === 'failed') {
                let reconnected = false;
                try { reconnected = await Blazor.reconnect(); } catch { return; } // still unreachable
                if (!reconnected && state.link === 'failed') {
                    let resumed = false;
                    try { resumed = await Blazor.resumeCircuit(); } catch { return; }
                    if (!resumed) reloadAsNewSession('The server no longer has this circuit');
                }
            }
        } finally {
            state.busy = false;
            render();
        }
    }

    function startTimer(interval) {
        stopTimer();
        timer = setInterval(() => { tick(); }, interval);
    }

    function stopTimer() {
        if (timer !== null) { clearInterval(timer); timer = null; }
    }

    // ------------------------------------------------------------------ events

    function reportVisibility() {
        if (!state.dotNetRef || state.link !== 'connected') return;
        // The circuit may disappear while this call is in flight; that is expected, not an error.
        state.dotNetRef.invokeMethodAsync('ReportTabVisibility', document.visibilityState).catch(() => { });
    }

    function onVisibilityChange() {
        if (document.visibilityState === 'hidden') {
            state.hiddenAt = Date.now();
            if (state.link === 'connected') note(`Tab hidden. Auto-pause in ${autoPauseSeconds()}s unless you return.`);
        } else {
            const away = state.hiddenAt ? Math.round((Date.now() - state.hiddenAt) / 1000) : null;
            state.hiddenAt = null;
            state.visibleAt = Date.now();
            if (away !== null) {
                note(state.link === 'connected' ? `Tab visible after ${away}s. Auto-pause timer cancelled; same circuit.`
                    : `Tab visible after ${away}s.`);
            }
            tick();
        }
        reportVisibility();
        render();
    }

    // Raised by Blazor on the reconnect element for every connection-state transition.
    function onReconnectStateChanged(event) {
        const detail = event.detail || {};
        switch (detail.state) {
            case 'show':
                state.link = 'reconnecting';
                state.attempt = 0;
                // Blazor raises 'show' at the start of a pause as well; only report a loss if it is one.
                setTimeout(() => {
                    if (state.link === 'reconnecting') note('Connection lost. The server still holds the circuit; reconnecting.');
                }, 500);
                break;
            case 'retrying':
                state.link = 'reconnecting';
                state.attempt = detail.currentAttempt || 0;
                break;
            case 'paused': {
                const kind = detail.remote ? 'admin' : 'tab';
                if (state.link === 'paused' && state.pauseKind === kind) break;
                state.link = 'paused';
                state.pauseKind = kind;
                state.hold = null;
                lastCircuitId = serverIds().circuitId;
                note(kind === 'admin' ? 'Paused by the server (administrative command). Waiting for an administrative resume.'
                    : 'Paused by the browser: the tab stayed hidden past the auto-pause delay.');
                startTimer(POLL_MS);
                break;
            }
            case 'hide': {
                const wasPaused = state.link === 'paused';
                const wasDown = state.link === 'reconnecting' || state.link === 'failed';
                state.link = 'connected';
                state.pauseKind = null;
                state.hold = null;
                state.attempt = 0;
                state.serverReachable = true;
                stopTimer();
                if (wasPaused) {
                    // The component re-renders with the new circuit ID shortly after this event.
                    setTimeout(() => {
                        const { circuitId } = serverIds();
                        if (state.link === 'paused') return; // resumed into a held session and paused again at once
                        note(circuitId && circuitId !== lastCircuitId
                            ? `Resumed: state restored into NEW circuit ${circuitId.slice(0, 10)}… (the paused circuit is gone).`
                            : 'Resumed.');
                    }, 400);
                } else if (wasDown) {
                    note('Reconnected to the SAME circuit.');
                }
                break;
            }
            case 'failed':
                state.link = 'failed';
                note('Reconnection attempts exhausted. Retrying while the tab is visible.');
                startTimer(RETRY_MS);
                break;
            case 'resume-failed':
            case 'rejected':
                reloadAsNewSession('The server no longer has this circuit or its persisted state');
                break;
        }
        render();
    }

    // ------------------------------------------------------------------ pause after a reconnect
    //
    // Defect in the .NET 11.0.0 Blazor client: when a connection closes it marks the circuit as
    // "paused" so that a later resume is allowed, but a successful reconnect to the same circuit
    // never clears that mark. From then on Blazor.pauseCircuit() and the server's pause request
    // both return as "already paused" without doing anything, so neither the hidden-tab auto-pause
    // nor an operator pause would work after any network blip.
    //
    // When that happens the same steps the client would have taken are carried out here on the
    // circuit's own connection: announce the pause to the reconnect UI, ask the server to persist
    // and release the circuit (the hub's PauseCircuit method), then close the connection. The
    // client's stale mark is then true again, and Blazor.resumeCircuit() works normally and clears it.
    let hub = null;

    async function pauseThroughHub(remote) {
        const connection = hub;
        if (!connection || connection.state !== 'Connected') return false;
        Blazor.defaultReconnectionHandler.onConnectionDown(
            { dialogId: 'components-reconnect-modal', maxRetries: 30 }, undefined, true, remote);
        let paused = false;
        try {
            paused = await connection.invoke('PauseCircuit');
        } catch (error) {
            note(`Pause could not be completed: ${error?.message ?? error}`);
        }
        if (!paused) {
            Blazor.defaultReconnectionHandler.onConnectionUp(); // nothing was paused; stay connected
            return false;
        }
        note('Blazor ignored the pause request (known client issue after a reconnect); paused on the connection directly.');
        try { await connection.stop(); } catch { /* already closing */ }
        return true;
    }

    const blazorIgnoredPause = connection => hub === connection && connection.state === 'Connected' && state.link !== 'paused';

    window.pizzaLab = {
        // Called by the JS initializer for every SignalR connection the circuit builds.
        onHubCreated(connection) {
            hub = connection;
            // The server's pause request. Blazor's own handler runs first; step in only if it did nothing.
            connection.on('JS.RequestPause', () => {
                setTimeout(() => { if (blazorIgnoredPause(connection)) pauseThroughHub(true); }, 300);
            });
        },

        // The AutoPause module calls Blazor.pauseCircuit, so the same check is applied to
        // browser-initiated pauses. Blazor assigns pauseCircuit when the circuit starts, which is
        // after the initializers run, so this is (re)applied whenever a circuit binds.
        guardPauseCircuit() {
            const pauseCircuit = window.Blazor?.pauseCircuit;
            if (!pauseCircuit || pauseCircuit.pizzaLabGuard) return;
            const guarded = async signal => {
                const connection = hub;
                const result = await pauseCircuit(signal);
                if (result && !signal?.aborted && connection && blazorIgnoredPause(connection)) return pauseThroughHub(false);
                return result;
            };
            guarded.pizzaLabGuard = true;
            window.Blazor.pauseCircuit = guarded;
        },

        // Called by the Home component after its first interactive render (initial load and every resume).
        bindCircuit(dotNetRef) {
            window.pizzaLab.guardPauseCircuit();
            state.dotNetRef = dotNetRef;
            if (state.link === 'starting') {
                state.link = 'connected';
                note(`Circuit ${(serverIds().circuitId || '').slice(0, 10)}… connected.`);
            }
            reportVisibility();
            render();
        }
    };

    // Blazor shows #blazor-error-ui for two different things: a real unhandled error on the circuit,
    // and a reconnect attempt that failed below the negotiate step (typical while the network is
    // flapping). The second is not an error in the app and the circuit is still alive on the server,
    // so it is reported in the monitor log instead of leaving a misleading error bar on the page.
    function watchErrorBar() {
        const bar = $('blazor-error-ui');
        if (!bar) return;
        const check = () => {
            if (bar.style.display !== 'block') return;
            if (state.link === 'reconnecting' || state.link === 'failed' || state.link === 'paused') {
                bar.style.display = 'none';
                note('A reconnect attempt failed at the network level. Not an application error; still retrying.');
            }
        };
        new MutationObserver(check).observe(bar, { attributes: true, attributeFilter: ['style'] });
    }

    function init() {
        watchErrorBar();
        window.addEventListener('offline', () => {
            note(state.link === 'connected'
                ? 'Browser reports the network is offline. The connection has not timed out yet (about 30s).'
                : 'Browser reports the network is offline.');
        });
        window.addEventListener('online', () => note('Browser reports the network is back.'));
        document.addEventListener('visibilitychange', onVisibilityChange);
        $('components-reconnect-modal')?.addEventListener('components-reconnect-state-changed', onReconnectStateChanged);
        window.addEventListener('pagehide', stopTimer);
        render();
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init, { once: true });
    else init();
})();
