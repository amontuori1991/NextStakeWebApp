// wwwroot/js/theme.js
(function () {
    function getTheme() {
        const cookieMatch = document.cookie.match(/(?:^|;\s*)theme=([^;]+)/);
        const cookieValue = cookieMatch ? decodeURIComponent(cookieMatch[1]) : null;
        const stored = cookieValue || localStorage.getItem("theme");

        return (stored === "light" || stored === "dark") ? stored : "dark";
    }

    function applyTheme(choice) {
        const applied = (choice === "light") ? "light" : "dark";
        document.documentElement.setAttribute("data-theme", applied);
    }

    // Applica SUBITO (prima possibile)
    applyTheme(getTheme());

    // opzionale: esponi funzioni se ti servono in future pagine
    window.NextStakeTheme = {
        get: getTheme,
        apply: applyTheme,
        set: function (choice) {
            const applied = (choice === "light") ? "light" : "dark";
            localStorage.setItem("theme", applied);
            document.cookie = "theme=" + encodeURIComponent(applied) +
                "; path=/; max-age=31536000; samesite=lax";
            applyTheme(applied);
        }
    };
})();
