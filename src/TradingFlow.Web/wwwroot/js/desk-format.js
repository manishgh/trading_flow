/* Desk number, time and symbol formatting; pure, shared by the Desk scripts. */
window.TradingFlowDeskFormat = (() => {
    'use strict';
    const moneyFormat = new Intl.NumberFormat(undefined, {style:'currency', currency:'USD', maximumFractionDigits:2});
    const fixedMoneyFormat = new Intl.NumberFormat(undefined, {style:'currency', currency:'USD', minimumFractionDigits:2, maximumFractionDigits:2});
    const numberFormat = new Intl.NumberFormat(undefined, {maximumFractionDigits:2});
    const exchangeDateFormat = new Intl.DateTimeFormat('en-US',{timeZone:'America/New_York',year:'numeric',month:'2-digit',day:'2-digit'});
    // Format available prices and measures without treating a missing value as zero.
    const money = value => value == null ? '—' : moneyFormat.format(value);
    const fixedMoney = value => typeof value === 'number' && Number.isFinite(value) ? fixedMoneyFormat.format(value) : '—';
    const normalizeChartSymbol = value => (value || '').trim().toUpperCase().replaceAll('.', '-');
    const number = value => value == null ? '—' : numberFormat.format(value);
    const when = value => value ? new Date(value).toLocaleString(undefined, {timeZone:'America/New_York'}) + ' NY · ' + new Date(value).toISOString() : 'Time unavailable';
    // Derive New York calendar dates from a verified UTC observation time, independently of the browser timezone.
    function exchangeDate(value){
        const stamp=Date.parse(value);if(!Number.isFinite(stamp))return null;
        const parts=exchangeDateFormat.formatToParts(stamp);
        const values=Object.fromEntries(parts.map(part=>[part.type,part.value]));
        return values.year+'-'+values.month+'-'+values.day;
    }

    return { money, fixedMoney, number, when, normalizeChartSymbol, exchangeDate };
})();
