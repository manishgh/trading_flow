// Desk filter presets. Owned by workstream C.
//
// A preset is a name attached to a desk query string. Applying one is a plain
// navigation to /TradeDesk plus that stored query - never a mutation, never a
// form submission, never an order. Storage is local to the browser; nothing here
// reaches the server.
//
// Loaded as a classic deferred script from _Layout, after command-palette.js, so
// window.TradingFlowPalette is normally present. It is still treated as optional.

(function () {
  "use strict";

  var STORAGE_KEY = "tradingflow.desk.presets";
  var MAX_PRESETS = 12;
  var MAX_NAME_LENGTH = 40;
  var PALETTE_GROUP = "View";

  // Desk view state worth restoring. Anything else in the stored query is
  // dropped on read, so a tampered or stale localStorage entry cannot smuggle
  // unexpected parameters into the navigation.
  var DESK_PARAMS = [
    "id", "source", "strategyId", "ticker", "search",
    "minPrice", "maxPrice", "maxSpreadBps", "sort", "dir", "env",
    "predictionMode", "predictionHorizon"
  ];

  // The desk is the only page carrying this row; everywhere else this script
  // does nothing at all.
  if (!document.querySelector(".desk-context-row")) {
    return;
  }

  var anchor = document.querySelector(".desk-controls") ||
    document.querySelector(".desk-context-row").closest("section");
  if (!anchor || typeof anchor.insertAdjacentElement !== "function") {
    return;
  }

  var strip = null;
  var chipList = null;
  var saveButton = null;
  var form = null;
  var nameInput = null;
  var note = null;
  var emptyHint = null;

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

  // Rebuild the query from an allowlist rather than trusting the stored text.
  function sanitizeQuery(query) {
    var source = new URLSearchParams(String(query || "").replace(/^[?]/, ""));
    var next = new URLSearchParams();
    DESK_PARAMS.forEach(function (key) {
      var value = readParam(source, key);
      if (value) {
        next.set(key, value);
      }
    });
    var text = next.toString();
    return text ? "?" + text : "";
  }

  function readPresets() {
    var raw = null;
    try {
      raw = window.localStorage.getItem(STORAGE_KEY);
    } catch (error) {
      return [];
    }
    if (!raw) {
      return [];
    }

    var parsed = null;
    try {
      parsed = JSON.parse(raw);
    } catch (error) {
      return [];
    }
    if (!Array.isArray(parsed)) {
      return [];
    }

    var seen = Object.create(null);
    var presets = [];
    parsed.forEach(function (entry) {
      if (!entry || typeof entry.name !== "string") {
        return;
      }
      var name = entry.name.trim().slice(0, MAX_NAME_LENGTH);
      var key = name.toLowerCase();
      if (!name || seen[key] || presets.length >= MAX_PRESETS) {
        return;
      }
      seen[key] = true;
      presets.push({ name: name, query: sanitizeQuery(entry.query) });
    });
    return presets;
  }

  function writePresets(presets) {
    try {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(presets));
      return true;
    } catch (error) {
      return false;
    }
  }

  function announce(message, tone) {
    if (!note) {
      return;
    }
    note.textContent = message || "";
    if (tone) {
      note.setAttribute("data-preset-tone", tone);
    } else {
      note.removeAttribute("data-preset-tone");
    }
  }

  // The palette is optional. Registering the full current list each time lets it
  // drop entries that were deleted here.
  function syncPalette(presets) {
    var palette = window.TradingFlowPalette;
    if (!palette || typeof palette.register !== "function") {
      return;
    }
    try {
      palette.register(presets.map(function (preset) {
        return {
          label: preset.name,
          hint: "Saved desk view",
          path: "/TradeDesk" + preset.query,
          group: PALETTE_GROUP
        };
      }), PALETTE_GROUP);
    } catch (error) {
      // A palette failure must never take the desk strip down with it.
    }
  }

  function applyPreset(query) {
    window.location.assign("/TradeDesk" + sanitizeQuery(query));
  }

  function renderChips(presets) {
    chipList.textContent = "";
    presets.forEach(function (preset) {
      var chip = document.createElement("li");
      chip.className = "tf-preset-chip";
      chip.setAttribute("data-preset-chip", preset.name);

      var apply = document.createElement("button");
      apply.type = "button";
      apply.className = "tf-preset-apply";
      apply.setAttribute("data-preset-apply", preset.name);
      apply.textContent = preset.name;
      apply.title = "Apply saved view: " + preset.name;
      apply.addEventListener("click", function () {
        applyPreset(preset.query);
      });

      var remove = document.createElement("button");
      remove.type = "button";
      remove.className = "tf-preset-delete";
      remove.setAttribute("data-preset-delete", preset.name);
      remove.setAttribute("aria-label", "Delete saved view " + preset.name);
      // Escaped rather than literal so the glyph survives any charset guess.
      remove.textContent = "×";
      remove.addEventListener("click", function () {
        deletePreset(preset.name);
      });

      chip.appendChild(apply);
      chip.appendChild(remove);
      chipList.appendChild(chip);
    });

    emptyHint.hidden = presets.length > 0;
  }

  function refresh(message, tone) {
    var presets = readPresets();
    renderChips(presets);
    syncPalette(presets);
    if (message !== undefined) {
      announce(message, tone);
    }
    return presets;
  }

  function savePreset(rawName) {
    var name = String(rawName || "").trim().slice(0, MAX_NAME_LENGTH);
    if (!name) {
      announce("Name the view before saving it.", "error");
      nameInput.focus();
      return false;
    }

    var presets = readPresets().filter(function (preset) {
      return preset.name.toLowerCase() !== name.toLowerCase();
    });
    presets.unshift({ name: name, query: sanitizeQuery(window.location.search) });

    if (!writePresets(presets.slice(0, MAX_PRESETS))) {
      announce("This browser refused to store the view.", "error");
      return false;
    }

    refresh("Saved view " + name + ".");
    return true;
  }

  function deletePreset(name) {
    var remaining = readPresets().filter(function (preset) {
      return preset.name.toLowerCase() !== String(name).toLowerCase();
    });
    if (!writePresets(remaining)) {
      announce("This browser refused to update stored views.", "error");
      return;
    }
    refresh("Deleted view " + name + ".");
    saveButton.focus();
  }

  function showForm() {
    form.hidden = false;
    saveButton.hidden = true;
    nameInput.value = "";
    announce("");
    nameInput.focus();
  }

  function hideForm(restoreFocus) {
    form.hidden = true;
    saveButton.hidden = false;
    nameInput.value = "";
    if (restoreFocus !== false) {
      saveButton.focus();
    }
  }

  // Built as buttons and an input rather than a <form> element on purpose: this
  // strip sits beside a GET filter form on a trading screen, and nothing here
  // should ever be capable of a submission.
  function buildStrip() {
    strip = document.createElement("section");
    strip.className = "tf-preset-strip";
    strip.setAttribute("data-desk-presets", "");
    strip.setAttribute("aria-label", "Saved desk views");
    strip.insertAdjacentHTML("afterbegin", [
      '<p class="tf-preset-heading" id="tf-preset-heading">Saved views</p>',
      '<ul class="tf-preset-chips" data-preset-chips aria-labelledby="tf-preset-heading"></ul>',
      '<button type="button" class="tf-preset-save" data-preset-save>Save current view</button>',
      '<div class="tf-preset-form" role="group" aria-label="Name this view" data-preset-form hidden>',
      '<label for="tf-preset-name">View name</label>',
      '<input id="tf-preset-name" type="text" maxlength="40" autocomplete="off"',
      ' placeholder="For example: Gappers under 20" data-preset-name />',
      '<button type="button" class="tf-preset-confirm" data-preset-confirm>Save</button>',
      '<button type="button" class="tf-preset-cancel" data-preset-cancel>Cancel</button>',
      "</div>",
      '<p class="tf-preset-note" data-preset-empty>No saved views yet. Filter the desk, then save the view to return to it in one click.</p>',
      '<p class="tf-preset-note" role="status" aria-live="polite" data-preset-note></p>'
    ].join(""));

    chipList = strip.querySelector("[data-preset-chips]");
    saveButton = strip.querySelector("[data-preset-save]");
    form = strip.querySelector("[data-preset-form]");
    nameInput = strip.querySelector("[data-preset-name]");
    note = strip.querySelector("[data-preset-note]");
    emptyHint = strip.querySelector("[data-preset-empty]");

    saveButton.addEventListener("click", showForm);
    strip.querySelector("[data-preset-confirm]").addEventListener("click", function () {
      if (savePreset(nameInput.value)) {
        hideForm(true);
      }
    });
    strip.querySelector("[data-preset-cancel]").addEventListener("click", function () {
      hideForm(true);
      announce("");
    });
    nameInput.addEventListener("keydown", function (event) {
      if (event.key === "Enter") {
        event.preventDefault();
        event.stopPropagation();
        if (savePreset(nameInput.value)) {
          hideForm(true);
        }
        return;
      }
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        hideForm(true);
        announce("");
      }
    });

    anchor.insertAdjacentElement("afterend", strip);
  }

  buildStrip();
  refresh();

  window.TradingFlowDeskPresets = {
    storageKey: STORAGE_KEY,
    list: readPresets,
    save: savePreset,
    remove: deletePreset,
    refresh: refresh
  };
})();
