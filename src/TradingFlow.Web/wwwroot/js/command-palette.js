// Command palette. Owned by workstream C.
//
// Bloomberg-style command-first navigation: Ctrl+K (Cmd+K on macOS) reaches any
// screen by name, and on the desk it also reaches any symbol already rendered on
// the page. Everything it does is a same-origin navigation. It never submits a
// form, never posts, and never touches an order control - a keystroke must not
// be able to place a trade.
//
// Loaded as a classic deferred script from _Layout, so no module syntax here and
// document.body is guaranteed to exist when this runs.

(function () {
  "use strict";

  // Static screen registry. Labels match the primary navigation so what an
  // operator reads in the top bar is what they can type.
  var SCREENS = [
    { label: "Desk", hint: "Watchlist, quotes and decisions", path: "/TradeDesk" },
    { label: "Positions", hint: "Running trades and open risk", path: "/RunningTrades" },
    { label: "Orders", hint: "Working and completed orders", path: "/Orders" },
    { label: "News", hint: "Catalyst feed", path: "/News" },
    { label: "Earnings", hint: "Reporting calendar and surprises", path: "/Earnings" },
    { label: "Research", hint: "Backtests and optimisation runs", path: "/Backtests" },
    { label: "Operations", hint: "Paper trading lab", path: "/Paper" },
    { label: "Wishlists", hint: "Symbol groups and universes", path: "/Wishlists" },
    { label: "Warmup", hint: "Indicator warm-up service", path: "/Warmup" }
  ];

  // Desk query parameters that survive a symbol jump. Anything outside this list
  // is dropped rather than forwarded, so a crafted URL cannot ride along.
  var DESK_PARAMS = [
    "id", "source", "strategyId", "search",
    "minPrice", "maxPrice", "maxSpreadBps", "sort", "dir", "env"
  ];

  var MAX_RESULTS = 40;
  var SYMBOL_PATTERN = /^[A-Z0-9][A-Z0-9.\-]{0,11}$/;

  var registered = [];
  var pool = [];
  var results = [];
  var activeIndex = 0;
  var isOpen = false;
  var lastFocused = null;

  var overlay = null;
  var dialog = null;
  var input = null;
  var list = null;

  function isTypingTarget(node) {
    if (!node || node.nodeType !== 1) {
      return false;
    }
    if (node.isContentEditable) {
      return true;
    }
    var tag = node.tagName;
    return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
  }

  // Same-origin, absolute-path navigation only. A protocol-relative "//host" or a
  // "/\host" would leave the site, so both are rejected along with anything that
  // is not a plain path.
  function isSafePath(path) {
    if (typeof path !== "string" || path.charAt(0) !== "/") {
      return false;
    }
    var second = path.charAt(1);
    if (second === "/" || second === "\\") {
      return false;
    }
    return !/[\s"'<>\\]/.test(path);
  }

  function readParam(params, key) {
    var wanted = key.toLowerCase();
    var found = "";
    params.forEach(function (value, name) {
      if (!found && name.toLowerCase() === wanted) {
        found = value;
      }
    });
    return found;
  }

  // Keep the operator's current desk context when jumping to a symbol: the group,
  // strategy, filters and sort stay put, only the inspected ticker changes.
  function deskSymbolPath(symbol) {
    var current = new URLSearchParams(window.location.search);
    var next = new URLSearchParams();
    DESK_PARAMS.forEach(function (key) {
      var value = readParam(current, key);
      if (value) {
        next.set(key, value);
      }
    });
    next.set("ticker", symbol);
    return "/TradeDesk?" + next.toString();
  }

  // Symbols come from rows the server already rendered; the palette issues no
  // requests of its own. [data-symbol-row] exists on the desk alone, and the
  // desktop table and mobile list carry the same ticker, hence the dedupe.
  function deskSymbolItems() {
    var seen = Object.create(null);
    var items = [];
    var rows = document.querySelectorAll("[data-symbol-row][data-symbol]");
    Array.prototype.forEach.call(rows, function (row) {
      var symbol = String(row.getAttribute("data-symbol") || "").trim().toUpperCase();
      if (!symbol || seen[symbol] || !SYMBOL_PATTERN.test(symbol)) {
        return;
      }
      seen[symbol] = true;
      items.push({
        label: symbol,
        hint: "Inspect on the desk",
        path: deskSymbolPath(symbol),
        group: "Symbol"
      });
    });
    return items;
  }

  function normalizeItem(raw) {
    if (!raw || typeof raw.label !== "string") {
      return null;
    }
    var label = raw.label.trim();
    var path = typeof raw.path === "string" ? raw.path.trim() : "";
    if (!label || !isSafePath(path)) {
      return null;
    }
    return {
      label: label.slice(0, 80),
      hint: typeof raw.hint === "string" ? raw.hint.trim().slice(0, 120) : "",
      path: path,
      group: typeof raw.group === "string" && raw.group.trim() ? raw.group.trim().slice(0, 24) : "Command"
    };
  }

  // Rank: whole-label prefix, then word prefix, then substring, then a hint or
  // group match. -1 means the item is out. Case-insensitive throughout.
  function score(item, query) {
    if (!query) {
      return 3;
    }
    var label = item.label.toLowerCase();
    if (label.indexOf(query) === 0) {
      return 0;
    }
    var words = label.split(/[^a-z0-9]+/);
    for (var index = 0; index < words.length; index += 1) {
      if (words[index] && words[index].indexOf(query) === 0) {
        return 1;
      }
    }
    if (label.indexOf(query) >= 0) {
      return 2;
    }
    if (item.hint.toLowerCase().indexOf(query) >= 0 || item.group.toLowerCase().indexOf(query) >= 0) {
      return 3;
    }
    return -1;
  }

  function buildShell() {
    var host = document.createElement("div");
    host.className = "tf-palette";
    host.hidden = true;
    host.setAttribute("data-command-palette", "");
    host.insertAdjacentHTML("afterbegin", [
      '<div class="tf-palette-scrim" data-palette-scrim></div>',
      '<div class="tf-palette-dialog" role="dialog" aria-modal="true"',
      ' aria-labelledby="tf-palette-title" data-palette-dialog>',
      '<h2 class="visually-hidden" id="tf-palette-title">Command palette</h2>',
      '<label class="visually-hidden" for="tf-palette-input">Jump to a screen or symbol</label>',
      '<input class="tf-palette-input" id="tf-palette-input" type="text" role="combobox"',
      ' autocomplete="off" autocorrect="off" spellcheck="false" aria-expanded="false"',
      ' aria-autocomplete="list" aria-controls="tf-palette-list"',
      ' placeholder="Jump to a screen or symbol" data-palette-input />',
      '<ul class="tf-palette-list" id="tf-palette-list" role="listbox"',
      ' aria-label="Palette results" data-palette-list></ul>',
      '<p class="tf-palette-foot">Up and Down arrows move, Enter opens, Escape closes.</p>',
      "</div>"
    ].join(""));

    document.body.appendChild(host);
    overlay = host;
    dialog = host.querySelector("[data-palette-dialog]");
    input = host.querySelector("[data-palette-input]");
    list = host.querySelector("[data-palette-list]");

    input.addEventListener("input", function () {
      activeIndex = 0;
      render();
    });
    overlay.addEventListener("keydown", onDialogKeyDown);
    overlay.addEventListener("mousedown", function (event) {
      if (!dialog.contains(event.target)) {
        event.preventDefault();
        closePalette(true);
      }
    });
    list.addEventListener("click", function (event) {
      var option = event.target.closest ? event.target.closest("[data-palette-index]") : null;
      if (!option) {
        return;
      }
      activate(Number(option.getAttribute("data-palette-index")));
    });
  }

  function renderEmptyRow() {
    var row = document.createElement("li");
    row.className = "tf-palette-empty";
    row.setAttribute("role", "presentation");
    row.setAttribute("data-palette-empty", "");
    row.textContent = "No matches. Try a screen name or a ticker.";
    list.appendChild(row);
  }

  function render() {
    var query = input.value.trim().toLowerCase();
    var scored = [];
    pool.forEach(function (item, index) {
      var rank = score(item, query);
      if (rank >= 0) {
        scored.push({ item: item, rank: rank, index: index });
      }
    });
    scored.sort(function (left, right) {
      return left.rank - right.rank || left.index - right.index;
    });

    results = scored.slice(0, MAX_RESULTS).map(function (entry) {
      return entry.item;
    });
    if (activeIndex >= results.length) {
      activeIndex = 0;
    }

    list.textContent = "";
    if (results.length === 0) {
      renderEmptyRow();
      input.setAttribute("aria-expanded", "false");
      input.removeAttribute("aria-activedescendant");
      return;
    }

    results.forEach(function (item, index) {
      var option = document.createElement("li");
      option.className = "tf-palette-option";
      option.id = "tf-palette-option-" + index;
      option.setAttribute("role", "option");
      option.setAttribute("data-palette-index", String(index));
      option.setAttribute("aria-selected", index === activeIndex ? "true" : "false");

      var text = document.createElement("span");
      text.className = "tf-palette-text";
      var label = document.createElement("span");
      label.className = "tf-palette-label";
      label.textContent = item.label;
      text.appendChild(label);
      if (item.hint) {
        var hint = document.createElement("span");
        hint.className = "tf-palette-hint";
        hint.textContent = item.hint;
        text.appendChild(hint);
      }

      var group = document.createElement("span");
      group.className = "tf-palette-group";
      group.textContent = item.group;

      option.appendChild(text);
      option.appendChild(group);
      list.appendChild(option);
    });

    input.setAttribute("aria-expanded", "true");
    input.setAttribute("aria-activedescendant", "tf-palette-option-" + activeIndex);
    scrollActiveIntoView();
  }

  function scrollActiveIntoView() {
    var option = list.children[activeIndex];
    if (!option || typeof option.getBoundingClientRect !== "function") {
      return;
    }
    var optionBox = option.getBoundingClientRect();
    var listBox = list.getBoundingClientRect();
    if (optionBox.top < listBox.top) {
      list.scrollTop -= listBox.top - optionBox.top;
    } else if (optionBox.bottom > listBox.bottom) {
      list.scrollTop += optionBox.bottom - listBox.bottom;
    }
  }

  function setSelection(index) {
    if (results.length === 0) {
      return;
    }
    activeIndex = Math.min(Math.max(index, 0), results.length - 1);
    Array.prototype.forEach.call(list.children, function (option, position) {
      if (option.getAttribute("role") === "option") {
        option.setAttribute("aria-selected", position === activeIndex ? "true" : "false");
      }
    });
    input.setAttribute("aria-activedescendant", "tf-palette-option-" + activeIndex);
    scrollActiveIntoView();
  }

  function moveSelection(delta) {
    if (results.length === 0) {
      return;
    }
    setSelection((activeIndex + delta + results.length) % results.length);
  }

  function focusableNodes() {
    return Array.prototype.filter.call(
      dialog.querySelectorAll('a[href], button:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])'),
      function (node) {
        return node.offsetParent !== null || node === input;
      });
  }

  function trapTab(event) {
    var nodes = focusableNodes();
    if (nodes.length === 0) {
      event.preventDefault();
      input.focus();
      return;
    }
    var first = nodes[0];
    var last = nodes[nodes.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  function onDialogKeyDown(event) {
    if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      closePalette(true);
      return;
    }
    if (event.key === "ArrowDown") {
      event.preventDefault();
      event.stopPropagation();
      moveSelection(1);
      return;
    }
    if (event.key === "ArrowUp") {
      event.preventDefault();
      event.stopPropagation();
      moveSelection(-1);
      return;
    }
    // Home and End are deliberately left alone: inside a text input they belong
    // to the caret, and stealing them would break ordinary editing.
    if (event.key === "Enter") {
      event.preventDefault();
      event.stopPropagation();
      activate(activeIndex);
      return;
    }
    if (event.key === "Tab") {
      trapTab(event);
    }
  }

  // The dialog is modal, so focus never legitimately leaves it while open.
  function guardFocus(event) {
    if (isOpen && dialog && !dialog.contains(event.target)) {
      event.stopPropagation();
      input.focus();
    }
  }

  function rebuildPool() {
    pool = SCREENS.map(function (screen) {
      return normalizeItem({
        label: screen.label,
        hint: screen.hint,
        path: screen.path,
        group: "Screen"
      });
    }).filter(Boolean)
      .concat(registered)
      .concat(deskSymbolItems());
  }

  function openPalette() {
    if (isOpen) {
      return;
    }
    lastFocused = document.activeElement;
    isOpen = true;
    overlay.hidden = false;
    input.value = "";
    activeIndex = 0;
    rebuildPool();
    render();
    input.focus();
    document.addEventListener("focusin", guardFocus, true);
  }

  function closePalette(restoreFocus) {
    if (!isOpen) {
      return;
    }
    isOpen = false;
    document.removeEventListener("focusin", guardFocus, true);
    overlay.hidden = true;
    input.value = "";
    input.setAttribute("aria-expanded", "false");
    input.removeAttribute("aria-activedescendant");
    list.textContent = "";
    results = [];
    activeIndex = 0;

    var target = lastFocused;
    lastFocused = null;
    if (restoreFocus !== false && target && document.contains(target) && typeof target.focus === "function") {
      target.focus();
    }
  }

  // Activation is navigation and nothing else.
  function activate(index) {
    var item = results[index];
    if (!item || !isSafePath(item.path)) {
      return;
    }
    closePalette(false);
    window.location.assign(item.path);
  }

  // Other surfaces contribute entries (saved desk views today). Registering a
  // batch replaces every previously registered entry in the same group, so a
  // caller can pass its full current list after a delete.
  function register(items, groupToReplace) {
    var incoming = (Array.isArray(items) ? items : []).map(normalizeItem).filter(Boolean);
    var groups = Object.create(null);
    if (typeof groupToReplace === "string" && groupToReplace.trim()) {
      groups[groupToReplace.trim()] = true;
    }
    incoming.forEach(function (item) {
      groups[item.group] = true;
    });

    registered = registered.filter(function (item) {
      return !groups[item.group];
    }).concat(incoming);

    if (isOpen) {
      rebuildPool();
      render();
    }
    return registered.length;
  }

  buildShell();

  // Capture phase on document: the shortcut belongs to the palette and no page
  // handler may swallow it. The typing guard keeps it out of the way of text
  // entry everywhere except the palette's own input, and because the palette
  // only claims Ctrl/Cmd chords it cannot shadow the desk's bare B/S shortcuts.
  document.addEventListener("keydown", function (event) {
    if (event.key !== "k" && event.key !== "K") {
      return;
    }
    if (!(event.ctrlKey || event.metaKey) || event.altKey) {
      return;
    }
    if (event.target !== input && isTypingTarget(event.target)) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    if (isOpen) {
      closePalette(true);
    } else {
      openPalette();
    }
  }, true);

  window.TradingFlowPalette = {
    register: register,
    open: openPalette,
    close: function () {
      closePalette(true);
    },
    isOpen: function () {
      return isOpen;
    }
  };
})();
