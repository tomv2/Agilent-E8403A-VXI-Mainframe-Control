let drivers = [];
let buses = [];
let inventory = [];
let discovery = [];
let selectedId = null;

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
        ${item.driverId === "hp.e1472a" ? `
        <p class="muted">
          Module 0 is the E1472A in this slot. Module 1 is the first E1473A
          expander in the following slot; module 2 is a second expander.
        </p>` : ""}
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

    renderOperations(item, operations);
  } catch (error) {
    $("#output").textContent =
      "Save assignments before loading commands.\n\n" + error.message;
  }
}

function clearCommands() {
  $("#commands").innerHTML = "";
}

function renderOperations(item, operations) {
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

    if (item.driverId === "hp.e1472a") {
      renderE1472Fields(item, fields);
    } else if (item.driverId === "hp.e1368a") {
      renderE1368Fields(fields);
    } else {
      renderGenericFields(fields, operation);
    }

    section.querySelector("[data-run]").addEventListener("click", () =>
      runOperation(item, operation, section));

    host.appendChild(section);
  }
}


function addField(host, labelText, input, helpText = "") {
  const wrapper = document.createElement("div");
  wrapper.className = "field";

  const label = document.createElement("label");
  label.textContent = labelText;
  wrapper.appendChild(label);
  wrapper.appendChild(input);

  if (helpText) {
    const help = document.createElement("div");
    help.className = "muted";
    help.textContent = helpText;
    wrapper.appendChild(help);
  }

  host.appendChild(wrapper);
}

function renderE1472Fields(item, host) {
  const baseSlot = item.address.physicalSlot;

  const module = document.createElement("select");
  module.dataset.parameter = "module";
  module.add(new Option(
    `E1472A base${baseSlot == null ? "" : ` — slot ${baseSlot}`}`,
    "0"));
  module.add(new Option(
    `E1473A expander 1${baseSlot == null ? "" : ` — slot ${baseSlot + 1}`}`,
    "1"));
  module.add(new Option(
    `E1473A expander 2${baseSlot == null ? "" : ` — slot ${baseSlot + 2}`}`,
    "2"));

  const bank = document.createElement("select");
  for (let value = 0; value <= 5; value++)
    bank.add(new Option(`Bank ${value}`, String(value)));

  const input = document.createElement("select");
  for (let value = 0; value <= 3; value++)
    input.add(new Option(`Input ${value}`, String(value)));

  const channel = document.createElement("input");
  channel.type = "hidden";
  channel.dataset.parameter = "channel";

  const updateChannel = () => {
    channel.value = String(Number(bank.value) * 10 + Number(input.value));
  };

  bank.addEventListener("change", updateChannel);
  input.addEventListener("change", updateChannel);
  updateChannel();

  addField(
    host,
    "Module",
    module,
    "Module 0 is the E1472A base. Module 1 is the first E1473A expander.");
  addField(host, "Bank", bank);
  addField(host, "Input", input);
  host.appendChild(channel);
}

function renderE1368Fields(host) {
  const input = document.createElement("select");
  input.dataset.parameter = "switch";

  input.add(new Option("Switch A (00)", "0"));
  input.add(new Option("Switch B (01)", "1"));
  input.add(new Option("Switch C (02)", "2"));

  addField(host, "RF switch", input);
}

function renderGenericFields(host, operation) {
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

      if (parameter.minimum !== null &&
          parameter.minimum !== undefined) {
        input.min = parameter.minimum;
        input.value = parameter.minimum;
      }

      if (parameter.maximum !== null &&
          parameter.maximum !== undefined) {
        input.max = parameter.maximum;
      }
    }

    input.dataset.parameter = parameter.name;

    addField(
      host,
      parameter.name + (parameter.required ? " *" : ""),
      input,
      parameter.description || "");
  }
}

async function runOperation(item, operation, section) {
  const parameters = {};

  section.querySelectorAll("[data-parameter]").forEach(input => {
    const value = input.value;
    const isNumeric =
      input.type === "number" ||
      input.type === "hidden" ||
      (input.tagName === "SELECT" && /^-?\d+$/.test(value));

    parameters[input.dataset.parameter] =
      isNumeric ? Number(value) : value;
  });

  const dryRun = !$("#liveMode").checked;

  if (!dryRun &&
      !confirm(
        `Run LIVE operation “${operation.name}” on ` +
        `${item.friendlyName || item.id}?`)) {
    return;
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

    $("#output").textContent = JSON.stringify(result, null, 2);
  } catch (error) {
    $("#output").textContent = error.message;
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
