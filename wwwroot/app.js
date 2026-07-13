document.addEventListener("DOMContentLoaded", () => {
    // API base URL
    const API_BASE = "";

    // DOM Elements
    const serverStatusDot = document.getElementById("server-status-dot");
    
    // SwitchBot elements
    const switchbotConnBadge = document.getElementById("switchbot-conn-badge");
    const switchbotBattery = document.getElementById("switchbot-battery");
    const switchbotBatteryBar = document.getElementById("switchbot-battery-bar");
    const switchbotState = document.getElementById("switchbot-state");
    const switchbotLog = document.getElementById("switchbot-log");
    const btnSwitchbotOn = document.getElementById("btn-switchbot-on");
    const btnSwitchbotOff = document.getElementById("btn-switchbot-off");
    
    // Tapo elements
    const btnRefreshTapo = document.getElementById("btn-refresh-tapo");
    const tapoLoading = document.getElementById("tapo-loading");
    const tapoNodesContainer = document.getElementById("tapo-nodes-container");
    
    // BLE Scanner elements
    const btnStartScan = document.getElementById("btn-start-scan");
    const scanProgressBar = document.getElementById("scan-progress-bar");
    const progressFill = scanProgressBar.querySelector(".progress-fill");
    const bleDevicesList = document.getElementById("ble-devices-list");
    
    // Commissioning elements
    const commissionForm = document.getElementById("commission-form");
    const btnSubmitCommission = document.getElementById("btn-submit-commission");
    const commissionFeedback = document.getElementById("commission-feedback");
    
    // Rename Modal elements
    const renameModal = document.getElementById("rename-modal");
    const renameInput = document.getElementById("rename-input");
    const renameTargetId = document.getElementById("rename-target-id");
    const btnRenameCancel = document.getElementById("btn-rename-cancel");
    const btnRenameSave = document.getElementById("btn-rename-save");

    // Local states
    let isScanning = false;
    
    // Hidden Outlets state
    const btnToggleHidden = document.getElementById("btn-toggle-hidden");
    let hiddenOutlets = JSON.parse(localStorage.getItem("omni_hidden_outlets") || "[]");
    let showHiddenState = localStorage.getItem("omni_show_hidden") === "true";

    if (showHiddenState) {
        btnToggleHidden.classList.add("active");
        btnToggleHidden.innerHTML = "🙈 Hidden";
    } else {
        btnToggleHidden.innerHTML = "👁️ Hidden";
    }

    // Clock removed from header

    // --- API HELPER WRAPPER ---
    async function apiRequest(endpoint, method = "GET", body = null) {
        const options = { method };
        if (body) {
            options.headers = { "Content-Type": "application/json" };
            options.body = JSON.stringify(body);
        }
        
        try {
            const response = await fetch(`${API_BASE}${endpoint}`, options);
            serverStatusDot.className = "pulse-indicator status-online";
            
            if (!response.ok) {
                const data = await response.json().catch(() => ({}));
                throw new Error(data.error || `Server returned status ${response.status}`);
            }
            
            return await response.json().catch(() => ({ success: true }));
        } catch (error) {
            console.error(`[API Error] ${endpoint}:`, error);
            // Flash status dot red on request failure
            serverStatusDot.className = "pulse-indicator";
            serverStatusDot.style.backgroundColor = "var(--color-danger)";
            throw error;
        }
    }

    // --- CLIPBOARD FALLBACK FOR NON-SECURE CONTEXTS (HTTP) ---
    async function copyToClipboard(text) {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            try {
                await navigator.clipboard.writeText(text);
                return true;
            } catch (err) {
                console.warn("Clipboard API failed, trying fallback...", err);
            }
        }
        
        try {
            const textarea = document.createElement("textarea");
            textarea.value = text;
            textarea.style.position = "fixed"; // Prevent scrolling to bottom
            textarea.style.opacity = "0";
            document.body.appendChild(textarea);
            textarea.select();
            const success = document.execCommand("copy");
            document.body.removeChild(textarea);
            return success;
        } catch (err) {
            console.error("Fallback copy failed: ", err);
            return false;
        }
    }

    // --- SWITCHBOT CONTROLLER ---
    async function updateSwitchBotStatus() {
        try {
            const data = await apiRequest("/api/switchbot/status");
            
            // Connection state badge
            switchbotConnBadge.textContent = data.connectionState;
            switchbotConnBadge.className = "badge";
            if (data.connectionState === "Connected") {
                switchbotConnBadge.classList.add("success");
            } else if (data.connectionState.includes("Scanning")) {
                switchbotConnBadge.classList.add("warning");
            } else {
                switchbotConnBadge.classList.add("danger");
            }

            // Battery level
            if (data.batteryLevel >= 0) {
                switchbotBattery.textContent = `${data.batteryLevel}%`;
                switchbotBatteryBar.style.width = `${data.batteryLevel}%`;
                if (data.batteryLevel < 20) {
                    switchbotBatteryBar.style.backgroundColor = "var(--color-danger)";
                } else if (data.batteryLevel < 50) {
                    switchbotBatteryBar.style.backgroundColor = "var(--color-warning)";
                } else {
                    switchbotBatteryBar.style.backgroundColor = "var(--color-success)";
                }
            } else {
                switchbotBattery.textContent = "--%";
                switchbotBatteryBar.style.width = "0%";
            }

            // Bot state
            switchbotState.textContent = data.botState;
            switchbotState.className = "value state-badge";
            if (data.botState === "ON") {
                switchbotState.style.color = "var(--color-success)";
            } else if (data.botState === "OFF") {
                switchbotState.style.color = "var(--text-secondary)";
            } else {
                switchbotState.style.color = "var(--accent-purple)";
            }

            // Status Log
            if (data.lastNotification) {
                switchbotLog.textContent = data.lastNotification;
            } else {
                switchbotLog.textContent = "No notifications received yet.";
            }

        } catch (error) {
            switchbotLog.textContent = `Error syncing status: ${error.message}`;
        }
    }

    // Event listeners for SwitchBot triggers
    btnSwitchbotOn.addEventListener("click", async () => {
        try {
            btnSwitchbotOn.disabled = true;
            switchbotLog.textContent = "Sending Turn ON trigger...";
            const res = await apiRequest("/api/switchbot/on", "POST");
            switchbotLog.textContent = res.message || "ON command queued.";
            setTimeout(updateSwitchBotStatus, 1500);
        } catch (error) {
            switchbotLog.textContent = `Action failed: ${error.message}`;
        } finally {
            btnSwitchbotOn.disabled = false;
        }
    });

    btnSwitchbotOff.addEventListener("click", async () => {
        try {
            btnSwitchbotOff.disabled = true;
            switchbotLog.textContent = "Sending Turn OFF trigger...";
            const res = await apiRequest("/api/switchbot/off", "POST");
            switchbotLog.textContent = res.message || "OFF command queued.";
            setTimeout(updateSwitchBotStatus, 1500);
        } catch (error) {
            switchbotLog.textContent = `Action failed: ${error.message}`;
        } finally {
            btnSwitchbotOff.disabled = false;
        }
    });

    // Start polling SwitchBot state every 3 seconds
    setInterval(updateSwitchBotStatus, 3000);
    updateSwitchBotStatus();


    // --- TAPO MATTER STRIP CONTROLLER ---
    async function loadTapoNodes() {
        tapoLoading.style.display = "flex";
        tapoNodesContainer.style.opacity = "0.4";
        
        try {
            const data = await apiRequest("/api/tapo/list");
            tapoNodesContainer.innerHTML = "";
            
            if (data.length === 0) {
                tapoNodesContainer.innerHTML = `
                    <div class="empty-state">
                        <p>No Tapo Matter devices found on the fabric.</p>
                        <p class="subtext">Pair a new device using the commissioning panel below.</p>
                    </div>
                `;
                return;
            }

            data.forEach(node => {
                const nodeBox = document.createElement("div");
                nodeBox.className = "tapo-node-box";
                
                // Header row
                const headerRow = document.createElement("div");
                headerRow.className = "node-title-row";
                headerRow.innerHTML = `
                    <div class="node-meta">
                        <span class="node-name-label" id="lbl-node-${node.nodeId}">${node.customName}</span>
                        <span class="edit-pencil" data-rename-id="${node.nodeId}" title="Rename Strip">✏️</span>
                    </div>
                    <span class="node-id-sub">Node ID: ${node.nodeId}</span>
                `;
                nodeBox.appendChild(headerRow);
                
                // Endpoints grid
                const epGrid = document.createElement("div");
                epGrid.className = "endpoints-grid";
                
                node.endpoints.forEach(ep => {
                    const isChecked = ep.state === "ON";
                    const formattedTypes = ep.types.join(", ");
                    const epKey = `${node.nodeId}_${ep.endpointId}`;
                    const isEpHidden = hiddenOutlets.includes(epKey);
                    
                    if (isEpHidden && !showHiddenState) {
                        return; // Skip rendering this endpoint card
                    }
                    
                    const epCard = document.createElement("div");
                    epCard.className = "endpoint-outlet-card";
                    if (isEpHidden) {
                        epCard.classList.add("faded-outlet");
                    }
                    
                    const isSafetyLocked = false; // PC safety lock disabled by user request
                    
                    epCard.innerHTML = `
                        <div class="ep-header">
                            <div class="ep-name-group">
                                <span class="ep-name" id="lbl-ep-${node.nodeId}-${ep.endpointId}">${ep.customName}</span>
                                ${showHiddenState ? `
                                    <span class="ep-idx">Outlet ${ep.endpointId} &bull; ${formattedTypes}</span>
                                ` : ''}
                            </div>
                            <label class="toggle-switch-wrapper">
                                <input type="checkbox" 
                                       id="toggle-${node.nodeId}-${ep.endpointId}"
                                       data-node="${node.nodeId}" 
                                       data-endpoint="${ep.endpointId}" 
                                       ${isChecked ? 'checked' : ''} 
                                       ${isSafetyLocked ? 'disabled' : ''}>
                                <span class="slider-knob"></span>
                            </label>
                        </div>
                        
                        ${isSafetyLocked ? `
                            <div class="safety-lock-banner" style="margin-bottom: 0.25rem;">
                                <span>⚠️ SAFETY LOCK</span> PC Host Power (Off Blocked)
                            </div>
                        ` : ''}

                        <div class="action-buttons" style="display: flex; gap: 0.5rem; width: 100%;">
                            <button class="btn btn-secondary btn-sm edit-pencil-ep" data-rename-id="${node.nodeId}_${ep.endpointId}" title="Rename Outlet" style="flex: 1;">✏️ Label</button>
                            <button class="btn btn-secondary btn-sm toggle-hide-ep" data-ep-key="${epKey}" title="${isEpHidden ? 'Unhide Outlet' : 'Hide Outlet'}" style="flex: 1;">
                                ${isEpHidden ? '👁️ Show' : '👁️‍🗨️ Hide'}
                            </button>
                        </div>
                    `;
                    epGrid.appendChild(epCard);
                });
                
                nodeBox.appendChild(epGrid);
                tapoNodesContainer.appendChild(nodeBox);
            });

            // Hook up toggles events
            tapoNodesContainer.querySelectorAll(".toggle-switch-wrapper input").forEach(input => {
                input.addEventListener("change", async (e) => {
                    const chk = e.target;
                    const nId = chk.dataset.node;
                    const epId = chk.dataset.endpoint;
                    const turnOn = chk.checked;
                    
                    chk.disabled = true; // Temporary disable to prevent click spamming
                    
                    try {
                        const path = `/api/tapo/${nId}/${epId}/${turnOn ? 'on' : 'off'}`;
                        const res = await apiRequest(path, "POST");
                        console.log(res.message);
                    } catch (error) {
                        alert(`Command failed: ${error.message}`);
                        // Revert check state on failure
                        chk.checked = !turnOn;
                    } finally {
                        chk.disabled = false;
                    }
                });
            });

            // Hook up hide/unhide button clicks
            tapoNodesContainer.querySelectorAll(".toggle-hide-ep").forEach(btn => {
                btn.addEventListener("click", () => {
                    const key = btn.dataset.epKey;
                    if (hiddenOutlets.includes(key)) {
                        hiddenOutlets = hiddenOutlets.filter(x => x !== key);
                    } else {
                        hiddenOutlets.push(key);
                    }
                    localStorage.setItem("omni_hidden_outlets", JSON.stringify(hiddenOutlets));
                    loadTapoNodes(); // Rerender
                });
            });

            // Hook up rename pencil clicks
            tapoNodesContainer.querySelectorAll(".edit-pencil, .edit-pencil-ep").forEach(pencil => {
                pencil.addEventListener("click", (e) => {
                    const id = pencil.dataset.renameId;
                    
                    // Retrieve existing name
                    let currentVal = "";
                    if (id.includes("_")) {
                        const [n, ep] = id.split("_");
                        currentVal = document.getElementById(`lbl-ep-${n}-${ep}`).textContent;
                    } else {
                        currentVal = document.getElementById(`lbl-node-${id}`).textContent;
                    }
                    
                    renameInput.value = currentVal;
                    renameTargetId.value = id;
                    renameModal.style.display = "flex";
                });
            });

            // Render shortcuts matrix
            renderShortcutsMatrix(data);

        } catch (error) {
            renderShortcutsMatrix([]);
            tapoNodesContainer.innerHTML = `
                <div class="alert-box danger">
                    <strong>Error listing Tapo Matter nodes:</strong> ${error.message}
                </div>
            `;
        } finally {
            tapoLoading.style.display = "none";
            tapoNodesContainer.style.opacity = "1";
        }
    }

    btnRefreshTapo.addEventListener("click", loadTapoNodes);
    loadTapoNodes(); // Initial trigger

    // --- SETUP PANEL TOGGLE ---
    const btnToggleSettings = document.getElementById("btn-toggle-settings");
    const settingsPanel = document.getElementById("settings-panel");

    btnToggleSettings.addEventListener("click", () => {
        const isHidden = settingsPanel.style.display === "none";
        settingsPanel.style.display = isHidden ? "grid" : "none";
        btnToggleSettings.classList.toggle("btn-primary", isHidden);
        btnToggleSettings.classList.toggle("btn-secondary", !isHidden);
    });

    btnToggleHidden.addEventListener("click", () => {
        showHiddenState = !showHiddenState;
        localStorage.setItem("omni_show_hidden", showHiddenState);
        if (showHiddenState) {
            btnToggleHidden.classList.add("active");
            btnToggleHidden.innerHTML = "🙈 Hidden";
        } else {
            btnToggleHidden.classList.remove("active");
            btnToggleHidden.innerHTML = "👁️ Hidden";
        }
        loadTapoNodes(); // Rerender
    });


    // --- CUSTOM RENAMING SYSTEM ---
    btnRenameCancel.addEventListener("click", () => {
        renameModal.style.display = "none";
    });

    btnRenameSave.addEventListener("click", async () => {
        const id = renameTargetId.value;
        const name = renameInput.value.trim();
        
        if (!name) {
            alert("Name cannot be empty.");
            return;
        }

        try {
            btnRenameSave.disabled = true;
            await apiRequest("/api/names", "POST", { id, name });
            renameModal.style.display = "none";
            
            // Reload UI
            loadTapoNodes();
        } catch (error) {
            alert(`Failed to save name: ${error.message}`);
        } finally {
            btnRenameSave.disabled = false;
        }
    });


    // --- BLUETOOTH LE SCANNER ---
    btnStartScan.addEventListener("click", async () => {
        if (isScanning) return;
        
        isScanning = true;
        btnStartScan.disabled = true;
        bleDevicesList.innerHTML = "";
        scanProgressBar.style.display = "flex";
        progressFill.style.width = "0%";
        
        // Mock progress bar loading animation (15s total)
        let progress = 0;
        const interval = setInterval(() => {
            progress += 1;
            progressFill.style.width = `${(progress / 15) * 100}%`;
            if (progress >= 15) {
                clearInterval(interval);
            }
        }, 1000);

        try {
            const data = await apiRequest("/api/bluetooth/scan");
            clearInterval(interval);
            progressFill.style.width = "100%";
            
            bleDevicesList.innerHTML = "";
            if (data.length === 0) {
                bleDevicesList.innerHTML = `<div class="empty-state"><p>No Bluetooth LE advertisements discovered nearby.</p></div>`;
            } else {
                data.forEach(dev => {
                    const row = document.createElement("div");
                    row.className = "ble-device-row";
                    row.innerHTML = `
                        <div class="ble-device-info">
                            <span class="ble-device-name">${dev.name}</span>
                            <span class="ble-device-mac" title="Click to copy">${dev.macAddress}</span>
                        </div>
                        <span class="rssi-indicator">${dev.rssi} dBm</span>
                    `;
                    
                    // Copy MAC to clipboard on click
                    row.querySelector(".ble-device-mac").addEventListener("click", (e) => {
                        navigator.clipboard.writeText(e.target.textContent);
                        
                        const origText = e.target.textContent;
                        e.target.textContent = "COPIED!";
                        setTimeout(() => {
                            e.target.textContent = origText;
                        }, 1200);
                    });

                    bleDevicesList.appendChild(row);
                });
            }
        } catch (error) {
            clearInterval(interval);
            bleDevicesList.innerHTML = `
                <div class="alert-box danger">
                    Failed to run scanner: ${error.message}
                </div>
            `;
        } finally {
            isScanning = false;
            btnStartScan.disabled = false;
            setTimeout(() => {
                scanProgressBar.style.display = "none";
            }, 1000);
        }
    });


    // --- MATTER COMMISSIONING ---
    commissionForm.addEventListener("submit", async (e) => {
        e.preventDefault();
        
        const setupCode = document.getElementById("setup-code").value.trim();
        const wifiSsid = document.getElementById("wifi-ssid").value.trim();
        const wifiPassword = document.getElementById("wifi-pass").value;

        commissionFeedback.style.display = "none";
        btnSubmitCommission.disabled = true;
        btnSubmitCommission.textContent = "Commissioning in progress...";

        try {
            const res = await apiRequest("/api/tapo/commission", "POST", {
                setupCode,
                wifiSsid: wifiSsid || null,
                wifiPassword: wifiPassword || null
            });

            commissionFeedback.style.display = "block";
            commissionFeedback.className = "alert-box success";
            commissionFeedback.innerHTML = `<strong>Success!</strong> ${res.message}`;
            
            // Reload node states
            loadTapoNodes();
            commissionForm.reset();
        } catch (error) {
            commissionFeedback.style.display = "block";
            commissionFeedback.className = "alert-box danger";
            commissionFeedback.innerHTML = `<strong>Failed:</strong> ${error.message}`;
        } finally {
            btnSubmitCommission.disabled = false;
            btnSubmitCommission.textContent = "Start Commissioning";
        }
    });

    // --- IOS SHORTCUTS ASSISTANT MATRIX RENDER ---
    function renderShortcutsMatrix(tapoNodes) {
        const tableBody = document.getElementById("shortcuts-table-body");
        const serverAddress = window.location.host || "localhost:5000";
        
        let html = "";
        
        // 1. SwitchBot Bot
        html += `
            <tr>
                <td data-label="Device / Outlet"><strong>SwitchBot Bot</strong><br><span style="font-size:0.75rem; color:var(--text-muted);">Direct BLE Control</span></td>
                <td data-label="Trigger ON URL"><span class="shortcut-url-code" title="Click to copy">http://${serverAddress}/api/switchbot/on</span></td>
                <td data-label="Trigger OFF URL"><span class="shortcut-url-code" title="Click to copy">http://${serverAddress}/api/switchbot/off</span></td>
                <td data-label="Toggle State URL"><span class="shortcut-url-code" style="color: var(--text-muted); cursor: not-allowed; background: none; border: none;">[N/A]</span></td>
            </tr>
        `;
        
        // 2. Tapo Outlets
        tapoNodes.forEach(node => {
            node.endpoints.forEach(ep => {
                const epKey = `${node.nodeId}_${ep.endpointId}`;
                const isEpHidden = hiddenOutlets.includes(epKey);

                const isSafetyLocked = false;
                const onUrl = `http://${serverAddress}/api/tapo/${node.nodeId}/${ep.endpointId}/on`;
                const offUrl = `http://${serverAddress}/api/tapo/${node.nodeId}/${ep.endpointId}/off`;
                const toggleUrl = `http://${serverAddress}/api/tapo/${node.nodeId}/${ep.endpointId}/toggle`;
                
                html += `
                    <tr class="${isEpHidden ? 'faded-row' : ''}">
                        <td data-label="Device / Outlet"><strong>${ep.customName}</strong><br><span style="font-size:0.75rem; color:var(--text-muted);">${node.customName} &bull; EP ${ep.endpointId}</span></td>
                        <td data-label="Trigger ON URL"><span class="shortcut-url-code" title="Click to copy">${onUrl}</span></td>
                        <td data-label="Trigger OFF URL">
                            ${isSafetyLocked ? `
                                <span class="shortcut-url-code" style="color: var(--color-danger); border-color: rgba(239,68,68,0.2); background: rgba(239,68,68,0.05); cursor: not-allowed;" title="Safety Lock: Off Command Blocked">[BLOCKED]</span>
                            ` : `
                                <span class="shortcut-url-code" title="Click to copy">${offUrl}</span>
                            `}
                        </td>
                        <td data-label="Toggle State URL">
                            ${isSafetyLocked ? `
                                <span class="shortcut-url-code" style="color: var(--color-danger); border-color: rgba(239,68,68,0.2); background: rgba(239,68,68,0.05); cursor: not-allowed;" title="Safety Lock: Toggle Command Blocked">[BLOCKED]</span>
                            ` : `
                                <span class="shortcut-url-code" title="Click to copy">${toggleUrl}</span>
                            `}
                        </td>
                    </tr>
                `;
            });
        });
        
        tableBody.innerHTML = html;
        
        // Hook copy events
        tableBody.querySelectorAll(".shortcut-url-code").forEach(span => {
            if (span.textContent.startsWith("http")) {
                span.addEventListener("click", async () => {
                    await copyToClipboard(span.textContent);
                    const origText = span.textContent;
                    span.textContent = "COPIED!";
                    span.style.color = "var(--color-success)";
                    setTimeout(() => {
                        span.textContent = origText;
                        span.style.color = "#34d399";
                    }, 1000);
                });
            }
        });
    }
});
