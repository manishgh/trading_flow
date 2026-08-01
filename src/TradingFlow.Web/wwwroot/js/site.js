(() => {
    const container = document.querySelector(".topbar-clocks");
    if (!container) return;

    const marketZone = container.dataset.marketTimeZone;
    const localZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    const marketCity = document.getElementById("market-clock-city");
    const marketTime = document.getElementById("market-clock-time");
    const localCity = document.getElementById("local-clock-city");
    const localTime = document.getElementById("local-clock-time");

    function cityFromZone(zone, fallback) {
        if (!zone) return fallback;
        const segment = zone.split("/").pop();
        return segment ? segment.replaceAll("_", " ") : fallback;
    }

    function formatTime(now, timeZone) {
        try {
            return new Intl.DateTimeFormat(undefined, {
                timeZone,
                hour: "2-digit",
                minute: "2-digit",
                hour12: false
            }).format(now);
        } catch {
            return "--:--";
        }
    }

    function update() {
        const now = new Date();
        marketCity.textContent = cityFromZone(marketZone, "Market");
        marketTime.textContent = formatTime(now, marketZone);
        localCity.textContent = cityFromZone(localZone, "Local");
        localTime.textContent = formatTime(now, localZone);
        marketTime.dateTime = now.toISOString();
        localTime.dateTime = now.toISOString();
    }

    update();
    window.setInterval(update, 30000);
})();
