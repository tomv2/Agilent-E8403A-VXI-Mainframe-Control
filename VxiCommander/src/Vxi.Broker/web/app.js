let drivers = [];
let buses = [];
let inventory = [];
let discovery = [];
let selectedId = null;
const chassisPreferences = JSON.parse(localStorage.getItem("vxiChassisPreferences") || "{}");

const visualState = new Map();
const $ = selector => document.querySelector(selector);

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll('"', "&quot;");
}

async function api(path, options) {
  const response = await fetch(path, options);
  const text = await response.text();

  let data;
  try {
    data = JSON.parse(text);
  } catch {
    data = text;
  }

  if (!response.ok) {
    throw new Error(
      typeof data === "string"
        ? data
        : data.error || text || `HTTP ${response.status}`);
  }

  return data;
}

async function initialise() {
  const status = await api("/api/status");
  drivers = status.drivers || [];
  buses = status.buses || [];
  inventory = await api("/api/inventory");

  $("#connectionStatus").textContent =
    `${status.status} · ${drivers.length} driver(s)`;

  renderModules();
}


function preferenceFor(item) {
  const existing = chassisPreferences[item.id] || {};
  const inferred = /\bE1473A\b/i.test(`${item.friendlyName || ""} ${item.model || ""}`) ? 1 : 0;
  const preference = {
    chassisSlots: Number(existing.chassisSlots || 13),
    attachedExpanders: Number(existing.attachedExpanders ?? inferred)
  };
  chassisPreferences[item.id] = preference;
  normalisePreference(item, preference);
  return preference;
}

function maximumExpanders(item, preference) {
  const slot = item.address.physicalSlot;
  if (slot == null) return 0;
  return Math.min(2, Math.max(0, preference.chassisSlots - slot));
}

function normalisePreference(item, preference) {
  preference.chassisSlots = Math.max(1, Number(preference.chassisSlots || 13));
  preference.attachedExpanders = Math.min(
    Math.max(0, Number(preference.attachedExpanders || 0)),
    maximumExpanders(item, preference));
}

function saveChassisPreferences() {
  localStorage.setItem("vxiChassisPreferences", JSON.stringify(chassisPreferences));
}

function driverOptions(selected) {
  return '<option value="">Choose driver…</option>' +
    drivers.map(driver =>
      `<option value="${escapeHtml(driver.id)}" ` +
      `${driver.id === selected ? "selected" : ""}>` +
      `${escapeHtml(driver.id)}</option>`).join("");
}

function parseDiscoveryCandidates() {
  const controller = discovery.find(
    endpoint => endpoint.kind === "e1406a-controller");

  if (!controller?.rawConfiguration)
    return [];

  const raw = controller.rawConfiguration;
  const dlisMarker = raw.indexOf("DLIS:");
  const infMarker = raw.indexOf("\nINF:");

  const dlisRaw =
    dlisMarker >= 0
      ? raw.slice(dlisMarker + 5, infMarker >= 0 ? infMarker : undefined)
      : raw;

  const informationRaw =
    infMarker >= 0
      ? raw.slice(infMarker + 5)
      : "";

  const parseRecords = value =>
    value.split(";").map(record => record.trim()).filter(Boolean);

  const dlisRecords = parseRecords(dlisRaw)
    .map(record => {
      const fields = record.split(",");
      const logicalAddress = Number(fields[0]);
      const legacySlot = Number(fields[4]);
      const description =
        (record.match(/"([^"]*)"\s*$/) || [])[1] || "";
      const secondaryAddress = Number(
        (description.match(/SECONDARY ADDR\s+(\d+)/i) || [])[1] || 15);

      return {
        logicalAddress,
        legacySlot: legacySlot >= 0 ? legacySlot : null,
        description,
        secondaryAddress
      };
    })
    .filter(record =>
      record.logicalAddress > 0 &&
      /SWITCH INSTALLED/i.test(record.description))
    .sort((left, right) =>
      left.logicalAddress - right.logicalAddress);

  const slotByLogicalAddress = new Map();

  for (const record of parseRecords(informationRaw)) {
    const fields = record.split(",");

    if (fields.length < 12)
      continue;

    const logicalAddress = Number(fields[0]);
    const physicalSlot = Number(fields[11]);

    if (logicalAddress > 0 && physicalSlot >= 0)
      slotByLogicalAddress.set(logicalAddress, physicalSlot);
  }

  return dlisRecords.map((record, index) => {
    const confirmedSlot =
      slotByLogicalAddress.get(record.logicalAddress) ??
      record.legacySlot ??
      null;

    return {
      id: `ladd-${record.logicalAddress}`,
      friendlyName: confirmedSlot == null
        ? `Unassigned card ${index + 1}`
        : `Physical slot ${confirmedSlot}`,
      driverId: "",
      manufacturer: "HP",
      model: "",
      enabled: true,
      manuallyAssigned: false,
      slotSource: slotByLogicalAddress.has(record.logicalAddress)
        ? "resource-manager"
        : confirmedSlot != null
          ? "legacy-resource-manager"
          : "unknown",
      address: {
        busId: controller.busId,
        primaryAddress: controller.primaryAddress,
        secondaryAddress: record.secondaryAddress,
        physicalSlot: confirmedSlot,
        logicalAddress: record.logicalAddress,
        switchboxCardNumber: index + 1
      }
    };
  });
}

function renderDiscoveryMainframe(candidates) {
  const host = $("#endpoints");
  const confirmed = new Map();
  const unknown = [];

  for (const candidate of candidates) {
    const slot = candidate.address.physicalSlot;

    if (slot != null && slot >= 0 && slot <= 12)
      confirmed.set(Number(slot), candidate);
    else
      unknown.push(candidate);
  }

  for (const item of inventory) {
    const slot = Number(item.address?.physicalSlot);

    if (slot >= 0 && slot <= 12 && !confirmed.has(slot))
      confirmed.set(slot, {
        ...item,
        slotSource: "saved"
      });
  }

  const slots = [];

  for (let slot = 12; slot >= 0; slot--) {
    const item = confirmed.get(slot);

    slots.push(`
      <button type="button"
        class="mainframe-slot ${item ? "occupied" : "empty"}"
        data-mainframe-slot="${slot}">
        <span class="mainframe-slot-number">${slot}</span>
        <span class="mainframe-slot-content">
          <strong>${item
            ? escapeHtml(item.friendlyName || item.model || item.id)
            : "Empty slot"}</strong>
          <small>${item
            ? `Physical slot ${slot}`
            : "Select a card, then click here"}</small>
        </span>
      </button>`);
  }

  host.innerHTML = `
    <section class="discovery-mainframe simplified">
      <div class="mainframe-heading">
        <div>
          <h3>Physical VXI mainframe</h3>
          <p>Select an unassigned card, then click the real slot number
             where that card is installed.</p>
        </div>
      </div>

      <div class="mainframe-layout">
        <div class="mainframe-chassis">${slots.join("")}</div>

        <aside class="unknown-device-panel">
          <h4>Cards needing a physical slot</h4>
          ${unknown.length
            ? unknown.map(candidate => `
              <label class="unknown-device">
                <input type="radio" name="unknown-device"
                  value="${candidate.address.logicalAddress}">
                <span>
                  <strong>
                    ${escapeHtml(
                      candidate.friendlyName ||
                      candidate.model ||
                      `Unassigned card ${candidate.address.switchboxCardNumber}`
                    )}
                  </strong>
                  <small>Choose this card, then click slot 0–12</small>
                </span>
              </label>`).join("")
            : `<p class="muted">All discovered cards have a physical slot.</p>`}
        </aside>
      </div>

      <details class="raw-discovery">
        <summary>Advanced discovery information</summary>
        <pre>${escapeHtml(JSON.stringify(discovery, null, 2))}</pre>
      </details>
    </section>`;

  host.querySelectorAll("[data-mainframe-slot]").forEach(button => {
    button.addEventListener("click", () => {
      const selected = host.querySelector(
        'input[name="unknown-device"]:checked');

      if (!selected)
        return;

      const logicalAddress = Number(selected.value);
      const slot = Number(button.dataset.mainframeSlot);
      const candidate = candidates.find(
        item => item.address.logicalAddress === logicalAddress);

      if (!candidate)
        return;

      let item = inventory.find(
        entry => entry.address?.logicalAddress === logicalAddress);

      if (!item) {
        item = structuredClone(candidate);
        inventory.push(item);
      }

      item.address.physicalSlot = slot;
      item.manuallyAssigned = true;
      item.slotSource = "user-confirmed";
      item.friendlyName =
        item.model || item.friendlyName || `Physical slot ${slot}`;

      renderModules();
      renderDiscoveryMainframe(candidates);

      $("#message").textContent =
        `Assigned the selected card to physical slot ${slot}.`;
    });
  });
}

async function discoverHardware() {
  $("#summary").textContent = "Scanning GPIB and E1406A resource manager…";
  discovery = await api("/api/discover", { method: "POST" });
  const candidates = parseDiscoveryCandidates();
  renderDiscoveryMainframe(candidates);
  const confirmed = candidates.filter(x => x.address.physicalSlot != null).length;
  $("#summary").textContent =
    `Found ${discovery.length} endpoint(s), ${candidates.length} switchbox logical device(s), ` +
    `${confirmed} slot(s) confirmed by the E1406A, and ${candidates.length - confirmed} requiring saved assignment.`;
}

function compareModulesBySlot(left, right) {
  const leftSlot = Number.isFinite(Number(left.address?.physicalSlot))
    ? Number(left.address.physicalSlot)
    : Number.POSITIVE_INFINITY;

  const rightSlot = Number.isFinite(Number(right.address?.physicalSlot))
    ? Number(right.address.physicalSlot)
    : Number.POSITIVE_INFINITY;

  if (leftSlot !== rightSlot)
    return leftSlot - rightSlot;

  const leftLogical = Number.isFinite(Number(left.address?.logicalAddress))
    ? Number(left.address.logicalAddress)
    : Number.POSITIVE_INFINITY;

  const rightLogical = Number.isFinite(Number(right.address?.logicalAddress))
    ? Number(right.address.logicalAddress)
    : Number.POSITIVE_INFINITY;

  if (leftLogical !== rightLogical)
    return leftLogical - rightLogical;

  return String(left.friendlyName || left.id)
    .localeCompare(String(right.friendlyName || right.id));
}

function buildModuleSlotBanner(item) {
  const slot = item.address?.physicalSlot;
  const assigned =
    slot !== null &&
    slot !== undefined &&
    slot !== "";

  const banner = document.createElement("div");
  banner.className =
    "module-slot-banner physical-slot-only" +
    (assigned ? "" : " unassigned");

  banner.innerHTML = `
    <div class="slot-number-block">
      <span class="slot-caption">PHYSICAL SLOT</span>
      <strong>${assigned ? escapeHtml(slot) : "?"}</strong>
    </div>

    <div class="slot-card-summary">
      <span class="slot-card-name">
        ${escapeHtml(item.friendlyName || item.model || item.id)}
      </span>
    </div>`;

  return banner;
}

function renderModules() {
  const host = $("#modules");
  host.innerHTML = "";

  for (const item of [...inventory].sort(compareModulesBySlot)) {
    const preference = preferenceFor(item);
    const maxExpanders = maximumExpanders(item, preference);
    const element = document.createElement("article");
    element.className =
      "module" + (item.id === selectedId ? " selected" : "");

    const location = item.address.physicalSlot !== null &&
      item.address.physicalSlot !== undefined
      ? `Slot ${item.address.physicalSlot}`
      : `Logical ${item.address.logicalAddress ?? "?"}`;

    element.innerHTML = `
      <div class="module-title">
        <strong>${escapeHtml(item.friendlyName || item.id)}</strong>
        <span class="badge">${escapeHtml(location)}</span>
      </div>
      <div class="module-meta">
        ${escapeHtml(item.driverId || "Driver not assigned")}
        · Card ${item.address.switchboxCardNumber ?? "?"}
      </div>

      <div class="field">
        <label>Driver</label>
        <select data-driver>${driverOptions(item.driverId)}</select>
      </div>

      <details>
        <summary>Advanced</summary>
        <div class="field">
          <label>Friendly name</label>
          <input data-name value="${escapeHtml(item.friendlyName)}">
        </div>
        <div class="field">
          <label>Model</label>
          <input data-model value="${escapeHtml(item.model)}">
        </div>
        <div class="advanced-grid">
          <div>
            <label>Physical slot</label>
            <input data-slot type="number" min="1"
              max="${preference.chassisSlots}"
              value="${item.address.physicalSlot ?? ""}">
          </div>
          <div>
            <label>Chassis slots</label>
            <input data-chassis-slots type="number" min="1"
              value="${preference.chassisSlots}">
          </div>
          <div>
            <label>Logical / card</label>
            <input disabled
              value="${item.address.logicalAddress ?? ""} / ${item.address.switchboxCardNumber ?? ""}">
          </div>
          <div>
            <label>PAD / SAD</label>
            <input disabled
              value="${item.address.primaryAddress} / ${item.address.secondaryAddress}">
          </div>
        </div>
        ${item.driverId === "hp.e1472a" ? `
        <div class="field">
          <label>Installed E1473A expanders</label>
          <select data-expanders>
            <option value="0" ${preference.attachedExpanders === 0 ? "selected" : ""}>None</option>
            ${maxExpanders >= 1 ? `<option value="1" ${preference.attachedExpanders === 1 ? "selected" : ""}>One — slot ${(item.address.physicalSlot ?? 0) + 1}</option>` : ""}
            ${maxExpanders >= 2 ? `<option value="2" ${preference.attachedExpanders === 2 ? "selected" : ""}>Two — slots ${(item.address.physicalSlot ?? 0) + 1} and ${(item.address.physicalSlot ?? 0) + 2}</option>` : ""}
          </select>
          <div class="muted">E1473A is passive and cannot be independently discovered. This explicit chassis setting avoids assuming an expander exists.</div>
        </div>` : ""}
        <button data-remove class="danger">Remove</button>
      </details>`;

    element.prepend(buildModuleSlotBanner(item));
    element.classList.add("physical-slot-only-card");

    for (const child of [...element.children]) {
      if (child.classList.contains("module-slot-banner"))
        continue;

      if (child.matches("details, label, select, input, button"))
        continue;

      if (child.querySelector("details, label, select, input, button"))
        continue;

      const text = child.textContent.trim();

      if (
        /slot\s*\d+/i.test(text) ||
        /logical\s*\d+/i.test(text) ||
        /card\s*\d+/i.test(text) ||
        /hp\.e\d+/i.test(text)
      ) {
        child.classList.add("engineering-summary");
      }
    }

    element.addEventListener("click", event => {
      if (event.target.matches("select,input,button,summary"))
        return;
      selectModule(item.id);
    });

    element.querySelector("[data-driver]").addEventListener("change", event => {
      item.driverId = event.target.value;
      renderModules();
      if (selectedId === item.id)
        selectModule(item.id);
    });

    element.querySelector("[data-name]").addEventListener("change", event => {
      item.friendlyName = event.target.value;
    });

    element.querySelector("[data-model]").addEventListener("change", event => {
      item.model = event.target.value;
    });

    element.querySelector("[data-slot]").addEventListener("change", event => {
      item.address.physicalSlot =
        event.target.value === "" ? null : Number(event.target.value);
      normalisePreference(item, preference);
      saveChassisPreferences();
      renderModules();
      if (selectedId === item.id)
        selectModule(item.id);
    });

    element.querySelector("[data-chassis-slots]").addEventListener("change", event => {
      preference.chassisSlots = Math.max(1, Number(event.target.value));
      normalisePreference(item, preference);
      saveChassisPreferences();
      renderModules();
      if (selectedId === item.id)
        selectModule(item.id);
    });

    const expanderSelect = element.querySelector("[data-expanders]");
    if (expanderSelect) {
      expanderSelect.addEventListener("change", event => {
        preference.attachedExpanders = Number(event.target.value);
        normalisePreference(item, preference);
        saveChassisPreferences();
        renderModules();
        if (selectedId === item.id)
          selectModule(item.id);
      });
    }

    element.querySelector("[data-remove]").addEventListener("click", () => {
      inventory = inventory.filter(candidate => candidate.id !== item.id);
      if (selectedId === item.id) {
        selectedId = null;
        clearCommands();
      }
      renderModules();
    });

    host.appendChild(element);
  }
}

function addManualModule() {
  const count = inventory.length + 1;

  inventory.push({
    id: `manual-${count}`,
    friendlyName: `Manual module ${count}`,
    driverId: "",
    manufacturer: "",
    model: "",
    enabled: true,
    manuallyAssigned: true,
    address: {
      busId: buses[0]?.id || "gpib0",
      primaryAddress: 10,
      secondaryAddress: 15,
      physicalSlot: null,
      logicalAddress: null,
      switchboxCardNumber: null
    }
  });

  renderModules();
}

async function saveAssignments() {
  const assigned = inventory.filter(item => item.driverId);

  if (!assigned.length) {
    showNotice("Assign at least one driver before saving.");
    return;
  }

  await api("/api/inventory", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(assigned)
  });

  inventory = assigned;
  renderModules();
  $("#output").textContent = `Saved ${assigned.length} module(s).`;
}

async function selectModule(id) {
  selectedId = id;
  renderModules();
  clearCommands();

  const item = inventory.find(candidate => candidate.id === id);
  $("#selectedTitle").textContent = item.friendlyName || item.id;

  if (!item.driverId) {
    $("#selectedInfo").textContent =
      "Choose a driver, then save assignments.";
    return;
  }

  $("#selectedInfo").textContent =
    `${item.driverId} · slot ${item.address.physicalSlot ?? "?"} · ` +
    `card ${item.address.switchboxCardNumber ?? "?"}`;

  try {
    const operations = await api("/api/describe", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        action: "describe",
        instrumentId: item.id
      })
    });

    if (item.driverId === "hp.e1368a") {
      renderE1368Panel(item, operations);
      appendDriverTools(item, operations, [
        "close", "open", "query-closed", "query-open"
      ]);
    } else if (item.driverId === "hp.e1472a") {
      renderE1472Panel(item, operations);
      appendDriverTools(item, operations, [
        "select", "restore", "query", "query-open"
      ]);
    } else {
      renderGenericOperations(item, operations);
    }
  } catch (error) {
    $("#output").textContent =
      "Save assignments before loading commands.\n\n" + error.message;
  }
}

function clearCommands() {
  $("#commands").innerHTML = "";
}

function operationById(operations, id) {
  const operation = operations.find(candidate => candidate.id === id);
  if (!operation)
    throw new Error(`Driver operation “${id}” is not available.`);
  return operation;
}

function stateFor(itemId) {
  if (!visualState.has(itemId)) {
    visualState.set(itemId, {
      relays: [null, null, null],
      mux: {}
    });
  }
  return visualState.get(itemId);
}

function renderE1368Panel(item, operations) {
  const host = $("#commands");
  host.innerHTML = `
    <section class="front-panel e1368-panel">
      <div class="front-panel-title">
        <div>
          <h3>HP E1368A microwave switches</h3>
          <p>Click either contact to move the relay pole.</p>
        </div>
        <span class="badge">3 × SPDT</span>
      </div>
      <div class="relay-grid"></div>
    </section>`;

  const grid = host.querySelector(".relay-grid");
  const state = stateFor(item.id);

  for (let relay = 0; relay < 3; relay++) {
    const card = document.createElement("article");
    card.className = "relay-card";
    card.dataset.relay = relay;
    card.innerHTML = relaySvg(relay, state.relays[relay]);

    card.querySelectorAll("[data-position]").forEach(contact => {
      contact.addEventListener("click", async () => {
        const position = contact.dataset.position;
        const operation = position === "port1"
          ? operationById(operations, "open")
          : operationById(operations, "close");

        const result = await executeOperation(
          item,
          operation,
          { switch: relay });

        if (result !== null) {
          state.relays[relay] = position;
          card.innerHTML = relaySvg(relay, position);
          bindRelayCardAgain(card, item, operations, relay);
        }
      });
    });

    grid.appendChild(card);
  }
}

function bindRelayCardAgain(card, item, operations, relay) {
  card.querySelectorAll("[data-position]").forEach(contact => {
    contact.addEventListener("click", async () => {
      const position = contact.dataset.position;
      const operation = position === "port1"
        ? operationById(operations, "open")
        : operationById(operations, "close");

      const result = await executeOperation(
        item,
        operation,
        { switch: relay });

      if (result !== null) {
        stateFor(item.id).relays[relay] = position;
        card.innerHTML = relaySvg(relay, position);
        bindRelayCardAgain(card, item, operations, relay);
      }
    });
  });
}

function relaySvg(relay, position) {
  const poleEnd = position === "port2"
    ? { x: 151, y: 92 }
    : { x: 151, y: 38 };

  return `
    <div class="relay-card-header">
      <strong>Switch ${String.fromCharCode(65 + relay)}</strong>
      <span class="relay-state">${position ? position.toUpperCase() : "UNKNOWN"}</span>
    </div>
    <svg viewBox="0 0 220 130" class="relay-svg" role="img"
      aria-label="SPDT relay switch ${relay}">
      <rect x="1" y="1" width="218" height="128" rx="12"
        class="panel-outline"/>
      <circle cx="50" cy="65" r="10" class="port common-port"/>
      <text x="50" y="91" text-anchor="middle">COMMON</text>

      <g data-position="port1" class="clickable-contact">
        <circle cx="170" cy="38" r="11"
          class="port ${position === "port1" ? "active-port" : ""}"/>
        <text x="170" y="20" text-anchor="middle">PORT 1</text>
      </g>

      <g data-position="port2" class="clickable-contact">
        <circle cx="170" cy="92" r="11"
          class="port ${position === "port2" ? "active-port" : ""}"/>
        <text x="170" y="119" text-anchor="middle">PORT 2</text>
      </g>

      <line x1="68" y1="65" x2="${poleEnd.x}" y2="${poleEnd.y}"
        class="relay-pole ${position ? "energised" : ""}"/>

    </svg>`;
}

function physicalMuxSlot(baseSlot, module) {
  if (baseSlot == null)
    return null;

  const slot = Number(baseSlot) + Number(module);
  return slot >= 0 && slot <= 12 ? slot : null;
}

function renderE1472Panel(item, operations) {
  const host = $("#commands");
  const baseSlot = item.address.physicalSlot;
  const preference = preferenceFor(item);
  normalisePreference(item, preference);

  const tabs = [
    { module: 0, label: `E1472A base${baseSlot == null ? "" : ` · slot ${baseSlot}`}` }
  ];

  for (let index = 1; index <= preference.attachedExpanders; index++) {
    tabs.push({
      module: index,
      label: `E1473A expander ${index}${baseSlot == null ? "" : ` · slot ${baseSlot + index}`}`
    });
  }

  host.innerHTML = `
    <section class="front-panel e1472-panel">
      <div class="front-panel-title">
        <div>
          <h3>HP E1472A / E1473A multiplexer</h3>
          <p>Banks are listed sequentially. Click an input and the route from COMMON is drawn clearly.</p>
        </div>
        <span class="badge">6 × 1-to-4 banks</span>
      </div>

      <div class="module-tabs">
        ${tabs.map((tab, index) => `
          <button class="module-tab ${index === 0 ? "active" : ""}" data-module="${tab.module}">
            ${escapeHtml(tab.label)}
          </button>`).join("")}
      </div>

      <div class="mux-bank-list"></div>
    </section>`;

  let selectedModule = 0;
  const panel = host.querySelector(".mux-bank-list");

  const render = () => {
    panel.innerHTML = "";
    for (let bank = 0; bank < 6; bank++)
      panel.appendChild(createMuxBank(item, operations, selectedModule, bank));
  };

  host.querySelectorAll("[data-module]").forEach(button => {
    button.addEventListener("click", () => {
      selectedModule = Number(button.dataset.module);
      host.querySelectorAll("[data-module]").forEach(candidate => candidate.classList.remove("active"));
      button.classList.add("active");
      render();
    });
  });

  render();
}

function createMuxBank(item, operations, module, bank) {
  const key = `${module}:${bank}`;
  const state = stateFor(item.id);
  const selectedInput = state.mux[key] ?? null;

  const bankElement = document.createElement("article");
  bankElement.className = "mux-bank-row";

  bankElement.innerHTML = `
    <div class="mux-bank-label">
      <strong>BANK ${bank}</strong>
      <span>Channels ${bank}0–${bank}3</span>
    </div>

    <div class="mux-diagram">
      ${muxBankSvg(bank, selectedInput)}
    </div>

    <button class="read-bank">Read bank</button>`;

  bankElement.querySelectorAll("[data-mux-input]").forEach(target => {
    target.addEventListener("click", async () => {
      const input = Number(target.dataset.muxInput);
      const channel = bank * 10 + input;
      const operation = operationById(operations, "select");

      const result = await executeOperation(
        item,
        operation,
        { module, channel });

      if (result !== null) {
        state.mux[key] = input;
        bankElement.replaceWith(
          createMuxBank(item, operations, module, bank));
      }
    });

    target.addEventListener("keydown", async event => {
      if (event.key !== "Enter" && event.key !== " ")
        return;

      event.preventDefault();
      target.dispatchEvent(new MouseEvent("click"));
    });
  });

  bankElement.querySelector(".read-bank").addEventListener("click", async () => {
    const query = operationById(operations, "query");
    let active = null;
    const readings = [];

    for (let input = 0; input < 4; input++) {
      const channel = bank * 10 + input;

      const result = await executeOperation(
        item,
        query,
        { module, channel },
        false);

      const response = extractInstrumentResponse(result);
      readings.push(
        `${String(channel).padStart(2, "0")}: ${response ?? "?"}`);

      if (String(response).trim() === "1")
        active = input;
    }

    state.mux[key] = active;
    $("#output").textContent =
      `Module ${module}, bank ${bank}\n` + readings.join("\n");

    bankElement.replaceWith(
      createMuxBank(item, operations, module, bank));
  });

  return bankElement;
}

function muxBankSvg(bank, selectedInput) {
  const commonX = 88;
  const commonY = 115;
  const portX = 690;
  const portYs = [40, 90, 140, 190];

  const selectedY =
    selectedInput == null ? null : portYs[selectedInput];

  return `
    <svg viewBox="0 0 780 230" class="mux-bank-svg"
      aria-label="Multiplexer bank ${bank}">

      <rect x="1" y="1" width="778" height="228" rx="16"
        class="mux-panel-background"/>

      <text x="${commonX}" y="38" text-anchor="middle"
        class="mux-svg-label">COMMON</text>

      <circle cx="${commonX}" cy="${commonY}" r="17"
        class="mux-common-port"/>

      ${selectedY == null ? `
        <line x1="${commonX + 29}" y1="${commonY}"
          x2="${commonX + 125}" y2="${commonY}"
          class="mux-path idle"/>`
        : `
        <polyline
          points="
            ${commonX + 29},${commonY}
            ${commonX + 220},${commonY}
            ${portX - 150},${selectedY}
            ${portX - 55},${selectedY}
          "
          class="mux-path active"/>`}

      ${portYs.map((y, input) => {
        const channel = `${bank}${input}`;
        const selected = selectedInput === input;

        return `
          <g class="mux-svg-button ${selected ? "selected" : ""}"
            data-mux-input="${input}"
            role="button"
            tabindex="0"
            aria-label="Select channel ${channel}">

            <rect x="${portX - 45}" y="${y - 21}"
              width="112" height="42" rx="10"
              class="mux-svg-button-box"/>

            <circle cx="${portX - 20}" cy="${y}" r="10"
              class="mux-svg-button-port"/>

            <text x="${portX + 18}" y="${y + 6}"
              text-anchor="middle"
              class="mux-svg-button-label">${channel}</text>
          </g>`;
      }).join("")}
    </svg>`;
}
function extractInstrumentResponse(result) {
  if (!Array.isArray(result) || !result.length)
    return null;
  return result[result.length - 1]?.response ?? null;
}


function appendDriverTools(item, operations, hiddenOperationIds = []) {
  const hidden = new Set(hiddenOperationIds);
  const available = operations.filter(operation => !hidden.has(operation.id));
  if (!available.length) return;

  const host = $("#commands");
  const details = document.createElement("details");
  details.className = "driver-tools";
  details.innerHTML = `
    <summary>
      <span>Tools &amp; diagnostics</span>
      <span class="tools-count">${available.length} command(s)</span>
    </summary>
    <div class="driver-tools-body"></div>`;

  const body = details.querySelector(".driver-tools-body");
  const queryIds = new Set([
    "identify", "card-type", "card-description", "card-options",
    "system-error", "operation-complete", "query-all", "query-bank",
    "query-module", "query-trigger-source", "query-arm-count",
    "query-scan-complete"
  ]);
  const disruptiveIds = new Set([
    "reset-card", "reset-switchbox", "recall-state", "initiate-scan",
    "abort-scan", "bus-trigger", "set-scan", "set-trigger-source",
    "set-arm-count", "clear-status", "self-test", "save-state"
  ]);

  for (const operation of available) {
    const card = document.createElement("section");
    card.className = "tool-command" +
      (disruptiveIds.has(operation.id) ? " disruptive" : "");

    card.innerHTML = `
      <div class="tool-command-header">
        <div>
          <strong>${escapeHtml(operation.name)}</strong>
          ${operation.description ? `<p>${escapeHtml(operation.description)}</p>` : ""}
        </div>
        <span class="tool-kind">${
          queryIds.has(operation.id) ? "Query" :
          disruptiveIds.has(operation.id) ? "Changes state" : "Command"
        }</span>
      </div>
      <div class="tool-fields"></div>
      <div class="tool-actions"></div>`;

    const fields = card.querySelector(".tool-fields");
    for (const parameter of operation.parameters || []) {
      const wrapper = document.createElement("label");
      wrapper.className = "tool-field";
      const title = document.createElement("span");
      title.textContent = parameter.name + (parameter.required ? " *" : "");

      let input;
      if (parameter.choices?.length) {
        input = document.createElement("select");
        for (const choice of parameter.choices) input.add(new Option(choice, choice));
      } else {
        input = document.createElement("input");
        const numeric = parameter.type === "integer" || parameter.type === "number";
        input.type = numeric ? "number" : "text";
        if (parameter.minimum != null) {
          input.min = parameter.minimum;
          input.value = parameter.minimum;
        }
        if (parameter.maximum != null) input.max = parameter.maximum;
        if (!numeric && parameter.description) input.placeholder = parameter.description;
      }

      input.dataset.toolParameter = parameter.name;
      input.dataset.parameterType = parameter.type || "string";
      wrapper.append(title, input);

      if (parameter.description) {
        const help = document.createElement("small");
        help.textContent = parameter.description;
        wrapper.appendChild(help);
      }
      fields.appendChild(wrapper);
    }

    const run = document.createElement("button");
    run.type = "button";
    run.textContent = queryIds.has(operation.id) ? "Run query" : "Run command";
    if (disruptiveIds.has(operation.id)) run.className = "danger-outline";

    run.addEventListener("click", async () => {
      const parameters = {};
      card.querySelectorAll("[data-tool-parameter]").forEach(input => {
        const type = input.dataset.parameterType;
        parameters[input.dataset.toolParameter] =
          type === "integer" || type === "number" ? Number(input.value) : input.value;
      });
      await executeOperation(item, operation, parameters);
    });

    card.querySelector(".tool-actions").appendChild(run);
    body.appendChild(card);
  }

  host.appendChild(details);
}

function renderGenericOperations(item, operations) {
  const host = $("#commands");
  host.innerHTML = "";

  for (const operation of operations) {
    const section = document.createElement("section");
    section.className = "operation";

    section.innerHTML = `
      <h3>${escapeHtml(operation.name)}</h3>
      ${operation.description
        ? `<p class="muted">${escapeHtml(operation.description)}</p>`
        : ""}
      <div data-fields></div>
      <div class="operation-actions">
        <button class="primary" data-run>Run</button>
      </div>`;

    const fields = section.querySelector("[data-fields]");

    for (const parameter of operation.parameters || []) {
      let input;

      if (parameter.choices?.length) {
        input = document.createElement("select");
        for (const choice of parameter.choices)
          input.add(new Option(choice, choice));
      } else {
        input = document.createElement("input");
        input.type =
          parameter.type === "integer" || parameter.type === "number"
            ? "number"
            : "text";

        if (parameter.minimum != null) {
          input.min = parameter.minimum;
          input.value = parameter.minimum;
        }

        if (parameter.maximum != null)
          input.max = parameter.maximum;
      }

      input.dataset.parameter = parameter.name;

      const wrapper = document.createElement("div");
      wrapper.className = "field";
      const label = document.createElement("label");
      label.textContent =
        parameter.name + (parameter.required ? " *" : "");
      wrapper.append(label, input);
      fields.appendChild(wrapper);
    }

    section.querySelector("[data-run]").addEventListener("click", async () => {
      const parameters = {};
      section.querySelectorAll("[data-parameter]").forEach(input => {
        const value = input.value;
        parameters[input.dataset.parameter] =
          input.type === "number" ||
          /^-?\d+$/.test(value)
            ? Number(value)
            : value;
      });
      await executeOperation(item, operation, parameters);
    });

    host.appendChild(section);
  }
}

async function executeOperation(
  item,
  operation,
  parameters,
  updateOutput = true) {

  const dryRun = !$("#liveMode").checked;

  if (!dryRun &&
      !confirm(
        `Run LIVE operation “${operation.name}” on ` +
        `${item.friendlyName || item.id}?`)) {
    return null;
  }

  try {
    const result = await api("/api/command", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        action: "operate",
        instrumentId: item.id,
        operationId: operation.id,
        parameters,
        dryRun
      })
    });

    if (updateOutput)
      $("#output").textContent = JSON.stringify(result, null, 2);

    return result;
  } catch (error) {
    $("#output").textContent = error.message;
    return null;
  }
}

function showNotice(message) {
  const notice = $("#notice");
  notice.textContent = message;
  notice.hidden = false;

  window.setTimeout(() => {
    notice.hidden = true;
  }, 5000);
}

$("#discoverButton").addEventListener("click", discoverHardware);
$("#saveButton").addEventListener("click", saveAssignments);
$("#manualButton").addEventListener("click", addManualModule);

initialise().catch(error => {
  $("#connectionStatus").textContent = "Error";
  $("#output").textContent = error.message;
});
