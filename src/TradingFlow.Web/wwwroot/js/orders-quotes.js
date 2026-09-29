/* Live quotes on the Orders screen: the journal's Last column for symbols with a
   working order, and the rail's quote for the symbol the ticket is open on.

   Uses the desk's symbol-set quote stream. Rows are looked up on every message
   rather than cached, because orders.js adds and removes journal rows in place
   as the order list changes. Quotes are display only; the ticket's server
   review reads its own quote. */
import { createNamedEventStream, normalizeTicker } from "./trading-flow-stream.js";

const shell = document.querySelector("[data-orders-stream-tickers]");
const tickers = String(shell?.dataset.ordersStreamTickers || "")
    .split(",")
    .map(normalizeTicker)
    .filter(Boolean);
const status = document.getElementById("OrdersQuoteState");

function setText(node, value) {
    const text = value || "--";
    if (node && node.textContent !== text) {
        node.textContent = text;
    }
}

function applyQuotes(quotes) {
    for (const quote of Array.isArray(quotes) ? quotes : []) {
        const ticker = normalizeTicker(quote.ticker);
        document.querySelectorAll(`[data-quote-row="${CSS.escape(ticker)}"] [data-quote-field="mid"]`)
            .forEach(node => setText(node, quote.midText));
        const rail = document.querySelector(`[data-selected-symbol="${CSS.escape(ticker)}"]`);
        if (rail) {
            setText(rail.querySelector('[data-selected-quote="bid"]'), quote.bidText);
            setText(rail.querySelector('[data-selected-quote="ask"]'), quote.askText);
            setText(rail.querySelector('[data-selected-quote="mid"]'), quote.midText);
        }
    }
}

if (tickers.length > 0) {
    createNamedEventStream(`/api/v1/desk/quotes/stream?tickers=${encodeURIComponent(tickers.join(","))}`, "quotes", {
        onMessage: applyQuotes,
        onState: (state, detail) => {
            if (status) {
                status.dataset.state = state;
                status.textContent = state === "connected" ? "Quotes live" : detail;
            }
        },
        onDecodeError: error => console.warn("Quote update rejected.", error)
    });
}
