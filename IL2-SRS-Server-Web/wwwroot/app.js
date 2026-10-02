// Loaded in <head> so the theme is applied before first paint and <local-time> is defined before the body renders.
(function () {
    // Theme: Desktop (follow the system setting) -> Light -> Dark, remembered per browser.
    var storageKey = 'il2srs.theme';
    var themes = ['desktop', 'light', 'dark'];
    var labels = { desktop: 'Desktop', light: 'Light', dark: 'Dark' };

    function readTheme() {
        try {
            var value = localStorage.getItem(storageKey);
            return themes.indexOf(value) >= 0 ? value : 'desktop';
        } catch (e) {
            return 'desktop';
        }
    }

    function applyTheme(theme) {
        var root = document.documentElement;
        root.setAttribute('data-theme-choice', theme);
        if (theme === 'desktop') {
            root.removeAttribute('data-theme');
        } else {
            root.setAttribute('data-theme', theme);
        }

        document.querySelectorAll('[data-theme-toggle]').forEach(function (button) {
            var next = themes[(themes.indexOf(theme) + 1) % themes.length];
            button.setAttribute('aria-label', 'Theme: ' + labels[theme] + '. Switch to ' + labels[next] + '.');
            button.title = 'Theme: ' + labels[theme] + ' (click for ' + labels[next] + ')';
        });
    }

    applyTheme(readTheme());
    document.addEventListener('DOMContentLoaded', function () { applyTheme(readTheme()); });

    document.addEventListener('click', function (event) {
        var button = event.target && event.target.closest && event.target.closest('[data-theme-toggle]');
        if (!button) {
            return;
        }

        var current = document.documentElement.getAttribute('data-theme-choice') || 'desktop';
        var next = themes[(themes.indexOf(current) + 1) % themes.length];
        try {
            localStorage.setItem(storageKey, next);
        } catch (e) {
            // Storage unavailable (private mode): the choice lasts for this page only.
        }

        applyTheme(next);
    });

    // Reconnect popup (#components-reconnect-modal in App.razor): when the server no longer knows this
    // page's session (it restarted), reconnecting cannot succeed, so reload instead of retrying.
    document.addEventListener('components-reconnect-state-changed', function (event) {
        var state = event.detail && event.detail.state;
        if (state === 'rejected' || state === 'resume-failed') {
            location.reload();
        }
    });

    // <local-time datetime="2026-10-02T20:22:00Z" format="datetime|seconds|time">: shows a UTC timestamp
    // in the browser's time zone. The server renders the element empty, so Blazor never overwrites the text.
    function pad(value) {
        return value < 10 ? '0' + value : String(value);
    }

    function formatLocal(date, format) {
        var time = pad(date.getHours()) + ':' + pad(date.getMinutes());
        if (format === 'time') {
            return time + ':' + pad(date.getSeconds());
        }

        var day = date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate());
        return format === 'seconds' ? day + ' ' + time + ':' + pad(date.getSeconds()) : day + ' ' + time;
    }

    if (window.customElements && !window.customElements.get('local-time')) {
        window.customElements.define('local-time', class extends HTMLElement {
            static get observedAttributes() { return ['datetime', 'format']; }
            connectedCallback() { this.render(); }
            attributeChangedCallback() { this.render(); }
            render() {
                var date = new Date(this.getAttribute('datetime'));
                if (isNaN(date.getTime())) {
                    return;
                }

                this.textContent = formatLocal(date, this.getAttribute('format'));
                this.title = date.toString();
            }
        });
    }
})();
