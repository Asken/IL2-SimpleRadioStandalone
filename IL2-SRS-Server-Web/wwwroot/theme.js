// Light / Dark / System theme, remembered per browser. Loaded in <head> so the theme is set before first paint.
(function () {
    var storageKey = 'il2srs.theme';

    function read() {
        try {
            var value = localStorage.getItem(storageKey);
            return value === 'light' || value === 'dark' ? value : 'system';
        } catch (e) {
            return 'system';
        }
    }

    function apply(theme) {
        if (theme === 'light' || theme === 'dark') {
            document.documentElement.setAttribute('data-theme', theme);
        } else {
            document.documentElement.removeAttribute('data-theme');
        }
    }

    function syncPickers() {
        var theme = read();
        document.querySelectorAll('[data-theme-picker]').forEach(function (picker) {
            picker.value = theme;
        });
    }

    apply(read());
    document.addEventListener('DOMContentLoaded', syncPickers);

    document.addEventListener('change', function (event) {
        var picker = event.target;
        if (!picker || !picker.matches || !picker.matches('[data-theme-picker]')) {
            return;
        }

        try {
            localStorage.setItem(storageKey, picker.value);
        } catch (e) {
            // Storage unavailable (private mode): the choice lasts for this page only.
        }

        apply(picker.value);
    });
})();
