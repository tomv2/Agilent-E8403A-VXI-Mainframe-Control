let drivers = [];
let buses = [];
let inventory = [];
let discovery = [];
let selectedId = null;

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

function driverOptions(selected) {
  return '<option value="">Choose driver…</option>' +
    drivers.map(driver =>
      `<option value="${escapeHtml(driver.id)}" ` +
      `${driver.id === selected ? "selected" : ""}>` +
      `${escapeHtml(driver.id)}</option>`).join("");
}

function parseDiscoveryCandidates() {
  const controller = discovery.find(
    endpoint =>
      endpoint.kind === "e1406a-controller" &&
      endpoint.rawConfiguration);

  if (!controller) return [];

  const records = controller.rawConfiguration
    .split(";")
    .map(value => value.trim())
    .filter(Boolean)
    .map(record => {
      const fields = record.split(",");
      const logicalAddress = Number(fields[0]);
      const rawSlot = Number(fields[4]);
      const description =
        (record.match(/"([^"]*)"\s*$/) || [])[1] || "";
      const secondaryAddress = Number(
        ((description.match(/SECONDARY ADDR\s+(\d+)/i) || [])[1]) || 15);

      return {
        logicalAddress,
        physicalSlot: rawSlot >= 0 ? rawSlot : null,
        description,
        secondaryAddress
      };
    })
    .filter(record =>
      record.logicalAddress > 0 &&
      /SWITCH INSTALLED/i.test(record.description))
    .sort((a, b) => a.logicalAddress - b.logicalAddress);

  return records.map((record, index) => ({
    id: `ladd-${record.logicalAddress}`,
    friendlyName: record.physicalSlot !== null
      ? `Slot ${record.physicalSlot}`
      : `Logical ${record.logicalAddress}`,
    driverId: "",
    manufacturer: "",
    model: "",
    enabled: true,
    manuallyAssigned: false,
    address: {
      busId: controller.busId,
      primaryAddress: controller.primaryAddress,
      secondaryAddress: record.secondaryAddress,
      physicalSlot: record.physicalSlot,
      logicalAddress: record.logicalAddress,
      switchboxCardNumber: index + 1
    }
  }));
}

async function discoverHardware() {
  $("#output").textContent = "Scanning…";

  discovery = await api("/api/discover", { method: "POST" });

  const candidates = parseDiscoveryCandidates();
  for (const candidate of candidates) {
    if (!inventory.some(item => item.id === candidate.id)) {
      inventory.push(candidate);
    }
  }

  $("#endpoints").innerHTML = discovery.length
    ? discovery.map(endpoint => `
      <div class="endpoint">
        <strong>${escapeHtml(endpoint.identification || endpoint.kind)}</strong>
        <span class="badge">
          PAD ${endpoint.primaryAddress} / SAD ${endpoint.secondaryAddress}
        </span>
      </div>`).join("")
    : '<span class="muted">No endpoints found.</span>';

  $("#output").textContent =
    `Found ${discovery.length} endpoint(s) and ` +
    `${candidates.length} module candidate(s).`;

  renderModules();
}

function renderModules() {
  const host = $("#modules");
  host.innerHTML = "";

  for (const item of inventory) {
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
            <input data-slot type="number" min="0"
              value="${item.address.physicalSlot ?? ""}">
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
        <button data-remove class="danger">Remove</button>
      </details>`;

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
      renderModules();
      if (selectedId === item.id)
        selectModule(item.id);
    });

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
    } else if (item.driverId === "hp.e1472a") {
      renderE1472Panel(item, operations);
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
    ? { x: 157, y: 92 }
    : { x: 157, y: 38 };

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

      <line x1="60" y1="65" x2="${poleEnd.x}" y2="${poleEnd.y}"
        class="relay-pole ${position ? "energised" : ""}"/>
      <circle cx="60" cy="65" r="5" class="pivot"/>
    </svg>`;
}

function renderE1472Panel(item, operations) {
  const host = $("#commands");
  const baseSlot = item.address.physicalSlot;

  host.innerHTML = `
    <section class="front-panel e1472-panel">
      <div class="front-panel-title">
        <div>
          <h3>HP E1472A / E1473A multiplexer</h3>
          <p>Select a module, then click one of the four input ports in a bank.</p>
        </div>
        <span class="badge">6 × 1-to-4 banks</span>
      </div>

      <div class="module-tabs">
        <button class="module-tab active" data-module="0">
          E1472A base${baseSlot == null ? "" : ` · slot ${baseSlot}`}
        </button>
        <button class="module-tab" data-module="1">
          E1473A expander 1${baseSlot == null ? "" : ` · slot ${baseSlot + 1}`}
        </button>
        <button class="module-tab" data-module="2">
          E1473A expander 2${baseSlot == null ? "" : ` · slot ${baseSlot + 2}`}
        </button>
      </div>

      <div class="mux-front-panel"></div>
    </section>`;

  let selectedModule = 0;
  const panel = host.querySelector(".mux-front-panel");

  const render = () => {
    panel.innerHTML = "";
    for (let bank = 0; bank < 6; bank++)
      panel.appendChild(createMuxBank(item, operations, selectedModule, bank));
  };

  host.querySelectorAll("[data-module]").forEach(button => {
    button.addEventListener("click", () => {
      selectedModule = Number(button.dataset.module);
      host.querySelectorAll("[data-module]")
        .forEach(candidate => candidate.classList.remove("active"));
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
  bankElement.className = "mux-bank";
  bankElement.innerHTML = `
    <div class="mux-bank-number">BANK ${bank}</div>
    <div class="mux-common">
      <span class="port-dot common"></span>
      <span>COMMON</span>
    </div>
    <div class="mux-route">
      <div class="mux-spine"></div>
      <div class="mux-pole ${selectedInput !== null ? "connected" : ""}"
        style="--selected-index:${selectedInput ?? 0}"></div>
    </div>
    <div class="mux-inputs"></div>
    <button class="read-bank">Read bank</button>`;

  const inputHost = bankElement.querySelector(".mux-inputs");

  for (let input = 0; input < 4; input++) {
    const channel = bank * 10 + input;
    const button = document.createElement("button");
    button.className =
      "mux-port" + (selectedInput === input ? " selected" : "");
    button.innerHTML = `
      <span class="port-dot"></span>
      <span>${String(channel).padStart(2, "0")}</span>`;
    button.title = `Module ${module}, bank ${bank}, input ${input}`;

    button.addEventListener("click", async () => {
      const operation = operationById(operations, "select");
      const result = await executeOperation(
        item,
        operation,
        { module, channel });

      if (result !== null) {
        state.mux[key] = input;
        const replacement = createMuxBank(item, operations, module, bank);
        bankElement.replaceWith(replacement);
      }
    });

    inputHost.appendChild(button);
  }

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
      readings.push(`${String(channel).padStart(2, "0")}: ${response ?? "?"}`);
      if (String(response).trim() === "1")
        active = input;
    }

    state.mux[key] = active;
    $("#output").textContent =
      `Module ${module}, bank ${bank}\n` + readings.join("\n");

    const replacement = createMuxBank(item, operations, module, bank);
    bankElement.replaceWith(replacement);
  });

  return bankElement;
}

function extractInstrumentResponse(result) {
  if (!Array.isArray(result) || !result.length)
    return null;
  return result[result.length - 1]?.response ?? null;
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
