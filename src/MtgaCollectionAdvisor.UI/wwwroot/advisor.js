// The page's own behaviour, shared by both hosts (#119). Loaded in <head> before the body, so
// the theme is set before the first paint and the window never flashes in the other theme.
(function () {
    var root = document.documentElement;

    // The theme follows the OS unless the player picked one; the status bar's button calls
    // advisorTheme.toggle(), and nothing about it goes through Blazor. The desktop window is
    // told the effective theme, so its title bar matches; a browser has no such channel.
    function effectiveTheme() {
        return root.dataset.theme
            || (matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark");
    }
    function reportTheme() {
        if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage("theme:" + effectiveTheme());
    }
    try { var saved = localStorage.getItem("theme"); if (saved) root.dataset.theme = saved; } catch (e) { }
    window.advisorTheme = {
        toggle: function () {
            var next = effectiveTheme() === "light" ? "dark" : "light";
            root.dataset.theme = next;
            try { localStorage.setItem("theme", next); } catch (e) { }
            reportTheme();
        }
    };
    reportTheme();
    matchMedia("(prefers-color-scheme: light)").addEventListener("change", reportTheme);

    // The What's new link (#97) glows on the first window after an update, until the pointer or
    // the keyboard reaches it; the version it was seen on is kept like the theme. The version is
    // the link's own (data-version), so this waits for Blazor to draw it. A dev build has no
    // release notes of its own, so it never glows.
    function watchReleaseNotes(link) {
        var version = link.dataset.version;
        if (!version || version === "dev") return;
        try { if (localStorage.getItem("releaseNotesSeen") === version) return; } catch (e) { }
        root.dataset.releaseNotesUnseen = "";
        function seen(e) {
            if (!e.target.closest || !e.target.closest(".whats-new")) return;
            delete root.dataset.releaseNotesUnseen;
            try { localStorage.setItem("releaseNotesSeen", version); } catch (e) { }
            document.removeEventListener("mouseover", seen);
            document.removeEventListener("focusin", seen);
        }
        document.addEventListener("mouseover", seen);
        document.addEventListener("focusin", seen);
    }
    var linkWatch = new MutationObserver(function () {
        var link = document.querySelector(".whats-new[data-version]");
        if (!link) return;
        linkWatch.disconnect();
        watchReleaseNotes(link);
    });
    linkWatch.observe(root, { childList: true, subtree: true });

    // Card and pack previews inside a dialog (#84) are position: fixed, so the dialog's scrolling
    // never clips them; this puts each beside the name under the mouse, on whichever side has
    // room, and inside the window. Plain DOM, no Blazor: CSS still decides when they show.
    var NAMES = ".modal-panel .card-name-hover, .modal-panel .pack-name-hover";
    function previewOf(name) {
        return name.classList.contains("pack-name-hover")
            ? name.closest(".pack") && name.closest(".pack").querySelector(".pack-preview")
            : name.querySelector(".card-preview");
    }
    function place(e) {
        var name = e.target.closest && e.target.closest(NAMES);
        var preview = name && previewOf(name);
        if (!preview) return;
        // After the hover rule has shown it, so it has a size to measure.
        requestAnimationFrame(function () {
            var r = name.getBoundingClientRect(), w = preview.offsetWidth, h = preview.offsetHeight, gap = 14, edge = 8;
            var x = r.right + gap;
            if (x + w > innerWidth - edge) x = r.left - gap - w;
            x = Math.max(edge, Math.min(x, innerWidth - w - edge));
            var y = Math.max(edge, Math.min(r.top + r.height / 2 - h / 2, innerHeight - h - edge));
            preview.style.left = x + "px";
            preview.style.top = y + "px";
        });
    }
    document.addEventListener("mouseover", place);
    document.addEventListener("focusin", place);
})();
