document.addEventListener("DOMContentLoaded", () => {
    // API base URL
    const API_BASE = "";

    // DOM Elements
    const serverStatusDot = document.getElementById("server-status-dot");
    const headerAirQuality = document.getElementById("header-air-quality");
    const headerHumidity = document.getElementById("header-humidity");
    const headerTemperature = document.getElementById("header-temperature");
    
    // SwitchBot elements
    const switchbotConnBadge = document.getElementById("switchbot-conn-badge");
    const switchbotBattery = document.getElementById("switchbot-battery");
    const switchbotBatteryBar = document.getElementById("switchbot-battery-bar");
    const switchbotState = document.getElementById("switchbot-state");
    const switchbotLog = document.getElementById("switchbot-log");
    const btnSwitchbotOn = document.getElementById("btn-switchbot-on");
    const btnSwitchbotOff = document.getElementById("btn-switchbot-off");

    // ASUS presence elements
    const presenceStateBadge = document.getElementById("presence-state-badge");
    const presencePhoneState = document.getElementById("presence-phone-state");
    const presenceLastCheck = document.getElementById("presence-last-check");
    const presenceNextCheck = document.getElementById("presence-next-check");
    const presenceMessage = document.getElementById("presence-message");
    const btnPresenceCheck = document.getElementById("btn-presence-check");
    const btnPresenceCheckApply = document.getElementById("btn-presence-check-apply");
    const presenceSettingsForm = document.getElementById("presence-settings-form");
    const presenceRouterHost = document.getElementById("presence-router-host");
    const presenceRouterPort = document.getElementById("presence-router-port");
    const presenceRouterUser = document.getElementById("presence-router-user");
    const presenceDeviceMac = document.getElementById("presence-device-mac");
    const presenceKeyBadge = document.getElementById("presence-key-badge");
    const btnPresenceGenerateKey = document.getElementById("btn-presence-generate-key");
    const presencePublicKeyWrap = document.getElementById("presence-public-key-wrap");
    const presencePublicKey = document.getElementById("presence-public-key");
    const btnPresenceCopyKey = document.getElementById("btn-presence-copy-key");
    const presenceSetupFeedback = document.getElementById("presence-setup-feedback");

    // Xiaomi purifier elements
    const xiaomiStateBadge = document.getElementById("xiaomi-state-badge");
    const xiaomiDevice = document.getElementById("xiaomi-device");
    const xiaomiLocalState = document.getElementById("xiaomi-local-state");
    const xiaomiPowerState = document.getElementById("xiaomi-power-state");
    const xiaomiMessage = document.getElementById("xiaomi-message");
    const btnXiaomiLogin = document.getElementById("btn-xiaomi-login");
    const xiaomiQrWrap = document.getElementById("xiaomi-qr-wrap");
    const xiaomiQr = document.getElementById("xiaomi-qr");
    const btnXiaomiTest = document.getElementById("btn-xiaomi-test");
    const btnXiaomiOn = document.getElementById("btn-xiaomi-on");
    const btnXiaomiOff = document.getElementById("btn-xiaomi-off");
    const xiaomiAutomation = document.getElementById("xiaomi-automation");
    const xiaomiLastAutomation = document.getElementById("xiaomi-last-automation");
    const xiaomiAirQuality = document.getElementById("xiaomi-air-quality");
    const xiaomiPm25 = document.getElementById("xiaomi-pm25");
    const xiaomiPm10 = document.getElementById("xiaomi-pm10");
    const xiaomiHumidity = document.getElementById("xiaomi-humidity");
    const xiaomiTemperature = document.getElementById("xiaomi-temperature");
    const xiaomiModeState = document.getElementById("xiaomi-mode-state");
    const xiaomiFanState = document.getElementById("xiaomi-fan-state");
    const xiaomiPlasmaState = document.getElementById("xiaomi-plasma-state");
    const xiaomiUvState = document.getElementById("xiaomi-uv-state");
    const xiaomiFaultState = document.getElementById("xiaomi-fault-state");
    const xiaomiFilterLife = document.getElementById("xiaomi-filter-life");
    const xiaomiFilterHours = document.getElementById("xiaomi-filter-hours");
    const xiaomiAlarmState = document.getElementById("xiaomi-alarm-state");
    const xiaomiChildLockState = document.getElementById("xiaomi-child-lock-state");
    const xiaomiBrightnessState = document.getElementById("xiaomi-brightness-state");
    const xiaomiFavoriteLevelState = document.getElementById("xiaomi-favorite-level-state");
    const xiaomiTemperatureUnitState = document.getElementById("xiaomi-temperature-unit-state");
    const xiaomiMotorRpm = document.getElementById("xiaomi-motor-rpm");
    const xiaomiRebootCause = document.getElementById("xiaomi-reboot-cause");
    const xiaomiIicErrors = document.getElementById("xiaomi-iic-errors");
    const xiaomiCountryCode = document.getElementById("xiaomi-country-code");
    const xiaomiFavoriteSquare = document.getElementById("xiaomi-favorite-square");
    const xiaomiAqiHeartbeat = document.getElementById("xiaomi-aqi-heartbeat");
    const xiaomiFilterTag = document.getElementById("xiaomi-filter-tag");
    const xiaomiFilterFactory = document.getElementById("xiaomi-filter-factory");
    const xiaomiFilterProduct = document.getElementById("xiaomi-filter-product");
    const xiaomiFilterManufactured = document.getElementById("xiaomi-filter-manufactured");
    const xiaomiFilterSerial = document.getElementById("xiaomi-filter-serial");
    const xiaomiModeControl = document.getElementById("xiaomi-mode-control");
    const xiaomiFanControl = document.getElementById("xiaomi-fan-control");
    const xiaomiPlasmaControl = document.getElementById("xiaomi-plasma-control");
    const xiaomiUvControl = document.getElementById("xiaomi-uv-control");
    const xiaomiChildLockControl = document.getElementById("xiaomi-child-lock-control");
    const xiaomiBrightnessControl = document.getElementById("xiaomi-brightness-control");
    const xiaomiFavoriteLevelControl = document.getElementById("xiaomi-favorite-level-control");
    const xiaomiTemperatureUnitControl = document.getElementById("xiaomi-temperature-unit-control");
    const xiaomiAlarmControl = document.getElementById("xiaomi-alarm-control");
    
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
    let tapoNodesCache = [];
    let routines = [];
    let currentRoutine = null;
    let routineNotice = { message: "", className: "" };
    let routinePollTimer = null;
    let presenceKeyGenerated = false;
    let xiaomiLoginPolling = null;

    // Dynamic Routine Builder elements
    const btnToggleRoutines = document.getElementById("btn-toggle-routines");
    const routinesPanel = document.getElementById("routines-panel");
    const btnRefreshRoutines = document.getElementById("btn-refresh-routines");
    const btnNewRoutine = document.getElementById("btn-new-routine");
    const routineList = document.getElementById("routine-list");
    const routineCount = document.getElementById("routine-count");
    const routineEditor = document.getElementById("routine-editor");
    
    // Hidden Outlets state
    const btnToggleHidden = document.getElementById("btn-toggle-hidden");
    let hiddenOutlets = [];
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
                throw new Error(data.error || data.detail || data.title || `Server returned status ${response.status}`);
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

    // --- ASUS AIMESH IPHONE PRESENCE ---
    const formatPresenceTime = (value, emptyText) => {
        if (!value) return emptyText;
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return emptyText;
        const now = new Date();
        const sameDay = date.getFullYear() === now.getFullYear()
            && date.getMonth() === now.getMonth()
            && date.getDate() === now.getDate();
        const time = date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
        return sameDay
            ? `Today, ${time}`
            : `${date.toLocaleDateString([], { month: "short", day: "numeric" })}, ${time}`;
    };

    function showPresenceFeedback(message, type = "success") {
        presenceSetupFeedback.style.display = "block";
        presenceSetupFeedback.className = `alert-box ${type}`;
        presenceSetupFeedback.textContent = message;
    }

    function renderPresenceStatus(data, populateForm = false) {
        const state = data.state || "Unknown";
        presenceStateBadge.textContent = state;
        presenceStateBadge.className = "badge";
        if (state === "Connected") presenceStateBadge.classList.add("success");
        else if (state === "Not connected") presenceStateBadge.classList.add("warning");
        else presenceStateBadge.classList.add("danger");

        presencePhoneState.textContent = state;
        presenceLastCheck.textContent = formatPresenceTime(data.lastCheckedAt, "Never");
        presenceNextCheck.textContent = formatPresenceTime(data.nextCheckAt, "--");
        presenceMessage.textContent = data.message || "No presence information is available.";

        presenceKeyGenerated = data.keyGenerated === true;
        presenceKeyBadge.textContent = presenceKeyGenerated ? "Key ready" : "No key";
        presenceKeyBadge.className = `badge ${presenceKeyGenerated ? "success" : "warning"}`;
        btnPresenceGenerateKey.textContent = presenceKeyGenerated ? "Regenerate SSH key" : "Generate SSH key";

        if (data.publicKey) {
            presencePublicKey.value = data.publicKey;
            presencePublicKeyWrap.style.display = "block";
        } else {
            presencePublicKey.value = "";
            presencePublicKeyWrap.style.display = "none";
        }

        if (populateForm) {
            presenceRouterHost.value = data.routerHost || "router.local";
            presenceRouterPort.value = data.port || 22;
            presenceRouterUser.value = data.userName || "";
            presenceDeviceMac.value = data.deviceMac || "00:00:00:00:00:00";
        }
    }

    async function loadPresenceStatus(populateForm = false) {
        try {
            renderPresenceStatus(await apiRequest("/api/asus-presence"), populateForm);
        } catch (error) {
            presenceStateBadge.textContent = "Unavailable";
            presenceStateBadge.className = "badge danger";
            presenceMessage.textContent = `Could not load presence status: ${error.message}`;
        }
    }

    async function savePresenceSettings(showFeedback = true) {
        const data = await apiRequest("/api/asus-presence/settings", "POST", {
            routerHost: presenceRouterHost.value.trim(),
            port: Number(presenceRouterPort.value),
            userName: presenceRouterUser.value.trim(),
            deviceMac: presenceDeviceMac.value.trim()
        });
        renderPresenceStatus(data, true);
        if (showFeedback) showPresenceFeedback("ASUS presence settings saved.");
        return data;
    }

    presenceSettingsForm.addEventListener("submit", async (event) => {
        event.preventDefault();
        const button = document.getElementById("btn-presence-save");
        button.disabled = true;
        try { await savePresenceSettings(); }
        catch (error) { showPresenceFeedback(error.message, "danger"); }
        finally { button.disabled = false; }
    });

    btnPresenceGenerateKey.addEventListener("click", async () => {
        const regenerate = presenceKeyGenerated;
        if (regenerate && !confirm("Generate a replacement key? The old public key in Merlin will stop working after you replace it.")) return;

        btnPresenceGenerateKey.disabled = true;
        try {
            const data = await apiRequest("/api/asus-presence/key", "POST", { regenerate });
            renderPresenceStatus(data);
            showPresenceFeedback("SSH key generated. Copy the public key into Merlin Authorized Keys.");
        } catch (error) {
            showPresenceFeedback(error.message, "danger");
        } finally {
            btnPresenceGenerateKey.disabled = false;
        }
    });

    btnPresenceCopyKey.addEventListener("click", async () => {
        const copied = await copyToClipboard(presencePublicKey.value);
        showPresenceFeedback(copied ? "Public key copied." : "Could not copy the public key.", copied ? "success" : "danger");
    });

    btnPresenceCheck.addEventListener("click", async () => {
        btnPresenceCheck.disabled = true;
        presenceMessage.textContent = "Checking the ASUS live client list…";
        try {
            await savePresenceSettings(false);
            renderPresenceStatus(await apiRequest("/api/asus-presence/check", "POST"));
        } catch (error) {
            presenceMessage.textContent = `Check failed: ${error.message}`;
            showPresenceFeedback(error.message, "danger");
        } finally {
            btnPresenceCheck.disabled = false;
        }
    });

    btnPresenceCheckApply.addEventListener("click", async () => {
        btnPresenceCheckApply.disabled = true;
        btnPresenceCheck.disabled = true;
        presenceMessage.textContent = "Checking presence and applying it to the purifier…";
        try {
            await savePresenceSettings(false);
            const result = await apiRequest("/api/asus-presence/check-and-apply", "POST");
            renderPresenceStatus(result.presence);
            renderXiaomiStatus(result.purifier);
            showPresenceFeedback(result.presence.state === "Connected"
                ? "iPhone connected — purifier turned on."
                : "iPhone absent — purifier turned off.");
        } catch (error) {
            presenceMessage.textContent = `Test failed: ${error.message}`;
            showPresenceFeedback(error.message, "danger");
            await loadPresenceStatus(false);
            await loadXiaomiStatus();
        } finally {
            btnPresenceCheckApply.disabled = false;
            btnPresenceCheck.disabled = false;
        }
    });

    loadPresenceStatus(true);
    setInterval(() => loadPresenceStatus(false), 30000);

    // --- XIAOMI PURIFIER: ONE-TIME LOGIN, THEN LOCAL CONTROL ---
    const airQualityLabels = ["Excellent", "Good", "Light pollution", "Moderate pollution", "Heavy pollution", "Severe pollution"];
    const modeLabels = ["Auto", "Sleep", "Favorite", "Manual"];
    const faultLabels = { 0: "No fault", 1: "PM sensor error", 2: "Temperature/humidity sensor error", 4: "No filter" };
    const brightnessLabels = ["Off", "Dim", "Normal"];
    const temperatureUnitLabels = { 1: "Celsius", 2: "Fahrenheit" };
    const rebootCauseLabels = { 0: "Hardware boot", 1: "User reboot", 2: "Update", 3: "Watchdog" };
    const countryCodeLabels = { 17230: "China", 17749: "EU", 21843: "US", 21591: "Taiwan", 19282: "Korea", 21835: "UK" };

    function displayValue(value, suffix = "") {
        return value === null || value === undefined || value === "" ? "--" : `${value}${suffix}`;
    }

    function renderHeaderRoomStatus(data) {
        headerAirQuality.textContent = data.airQuality === null || data.airQuality === undefined
            ? "--"
            : (airQualityLabels[data.airQuality] || `Level ${data.airQuality}`);
        headerHumidity.textContent = displayValue(data.humidity, "%");
        headerTemperature.textContent = displayValue(data.temperature === null || data.temperature === undefined ? null : Number(data.temperature).toFixed(1), "°C");
    }

    function renderXiaomiStatus(data) {
        renderHeaderRoomStatus(data);
        xiaomiDevice.textContent = `${data.deviceName || "Smart Air Purifier Elite"} · ${data.ipAddress || "192.168.1.106"}`;
        xiaomiLocalState.textContent = data.localValidated ? "Working" : (data.tokenStored ? "Ready to test" : "No token");
        xiaomiPowerState.textContent = data.power === true ? "On" : data.power === false ? "Off" : "Unknown";
        xiaomiMessage.textContent = data.message || "No Xiaomi status is available.";
        xiaomiAirQuality.textContent = data.airQuality === null || data.airQuality === undefined ? "--" : (airQualityLabels[data.airQuality] || `Level ${data.airQuality}`);
        xiaomiPm25.textContent = displayValue(data.pm25, " μg/m³");
        xiaomiPm10.textContent = displayValue(data.pm10, " μg/m³");
        xiaomiHumidity.textContent = displayValue(data.humidity, "%");
        xiaomiTemperature.textContent = displayValue(data.temperature === null || data.temperature === undefined ? null : Number(data.temperature).toFixed(1), "°C");
        xiaomiModeState.textContent = data.mode === null || data.mode === undefined ? "--" : (modeLabels[data.mode] || `Value ${data.mode}`);
        xiaomiFanState.textContent = displayValue(data.fanLevel, data.fanLevel === null || data.fanLevel === undefined ? "" : " / 3");
        xiaomiPlasmaState.textContent = data.plasma === true ? "On" : data.plasma === false ? "Off" : "--";
        xiaomiUvState.textContent = data.uv === true ? "On" : data.uv === false ? "Off" : "--";
        xiaomiFaultState.textContent = data.fault === null || data.fault === undefined ? "--" : (faultLabels[data.fault] || `Code ${data.fault}`);
        xiaomiFilterLife.textContent = displayValue(data.filterLife, "%");
        xiaomiFilterHours.textContent = displayValue(data.filterUsedHours, " hours");
        xiaomiAlarmState.textContent = data.alarm === true ? "On" : data.alarm === false ? "Off" : "--";
        xiaomiChildLockState.textContent = data.childLock === true ? "On" : data.childLock === false ? "Off" : "--";
        xiaomiBrightnessState.textContent = data.screenBrightness === null || data.screenBrightness === undefined ? "--" : (brightnessLabels[data.screenBrightness] || `Value ${data.screenBrightness}`);
        xiaomiFavoriteLevelState.textContent = displayValue(data.favoriteLevel, data.favoriteLevel === null || data.favoriteLevel === undefined ? "" : " / 14");
        xiaomiTemperatureUnitState.textContent = temperatureUnitLabels[data.temperatureDisplayUnit] || displayValue(data.temperatureDisplayUnit);
        xiaomiMotorRpm.textContent = displayValue(data.motorRpm, " rpm");
        xiaomiRebootCause.textContent = data.rebootCause === null || data.rebootCause === undefined ? "--" : (rebootCauseLabels[data.rebootCause] || `Code ${data.rebootCause}`);
        xiaomiIicErrors.textContent = displayValue(data.iicErrorCount);
        xiaomiCountryCode.textContent = data.countryCode === null || data.countryCode === undefined ? "--" : (countryCodeLabels[data.countryCode] || `Code ${data.countryCode}`);
        xiaomiFavoriteSquare.textContent = displayValue(data.favoriteSquare);
        xiaomiAqiHeartbeat.textContent = displayValue(data.aqiUpdateHeartbeat);
        xiaomiFilterTag.textContent = displayValue(data.filterTag);
        xiaomiFilterFactory.textContent = displayValue(data.filterFactoryId);
        xiaomiFilterProduct.textContent = displayValue(data.filterProductId);
        xiaomiFilterManufactured.textContent = displayValue(data.filterManufacturedAt);
        xiaomiFilterSerial.textContent = displayValue(data.filterSerialNumber);

        const ready = data.localValidated === true;
        xiaomiStateBadge.textContent = ready ? "Local ready" : data.loginRunning ? "Waiting for scan" : data.tokenStored ? "Token ready" : "Not set up";
        xiaomiStateBadge.className = `badge ${ready ? "success" : data.loginRunning ? "warning" : data.tokenStored ? "warning" : "danger"}`;
        btnXiaomiLogin.disabled = data.loginRunning === true;
        btnXiaomiLogin.textContent = data.loginRunning ? "Waiting for scan…" : data.tokenStored ? "Sign in again" : "Show QR code";
        btnXiaomiTest.disabled = !data.tokenStored || data.loginRunning;
        btnXiaomiOn.disabled = !ready;
        btnXiaomiOff.disabled = !ready;
        xiaomiModeControl.disabled = !ready;
        xiaomiFanControl.disabled = !ready;
        xiaomiPlasmaControl.disabled = !ready;
        xiaomiUvControl.disabled = !ready;
        xiaomiChildLockControl.disabled = !ready;
        xiaomiBrightnessControl.disabled = !ready;
        xiaomiFavoriteLevelControl.disabled = !ready;
        xiaomiTemperatureUnitControl.disabled = !ready;
        xiaomiAlarmControl.disabled = !ready;
        if (data.mode !== null && data.mode !== undefined) xiaomiModeControl.value = String(data.mode);
        if (data.fanLevel !== null && data.fanLevel !== undefined) xiaomiFanControl.value = String(data.fanLevel);
        if (data.plasma !== null && data.plasma !== undefined) xiaomiPlasmaControl.checked = data.plasma;
        if (data.uv !== null && data.uv !== undefined) xiaomiUvControl.checked = data.uv;
        if (data.childLock !== null && data.childLock !== undefined) xiaomiChildLockControl.checked = data.childLock;
        if (data.screenBrightness !== null && data.screenBrightness !== undefined) xiaomiBrightnessControl.value = String(data.screenBrightness);
        if (data.favoriteLevel !== null && data.favoriteLevel !== undefined) xiaomiFavoriteLevelControl.value = String(data.favoriteLevel);
        if (data.temperatureDisplayUnit !== null && data.temperatureDisplayUnit !== undefined) xiaomiTemperatureUnitControl.value = String(data.temperatureDisplayUnit);
        if (data.alarm !== null && data.alarm !== undefined) xiaomiAlarmControl.checked = data.alarm;
        xiaomiAutomation.disabled = !ready;
        xiaomiAutomation.checked = data.automationEnabled === true;
        xiaomiLastAutomation.textContent = data.lastAutomationAt
            ? `Last hourly action: ${formatPresenceTime(data.lastAutomationAt, "")}. ${data.lastAutomationResult || ""}`
            : "Last hourly action: none yet.";

        xiaomiQrWrap.style.display = data.qrReady ? "block" : "none";
        if (data.qrReady) xiaomiQr.src = `/api/xiaomi-purifier/qr?t=${Date.now()}`;
        if (!data.loginRunning && xiaomiLoginPolling) {
            clearInterval(xiaomiLoginPolling);
            xiaomiLoginPolling = null;
        }
    }

    async function loadXiaomiStatus() {
        try { renderXiaomiStatus(await apiRequest("/api/xiaomi-purifier")); }
        catch (error) { xiaomiMessage.textContent = `Could not load purifier status: ${error.message}`; }
    }

    btnXiaomiLogin.addEventListener("click", async () => {
        btnXiaomiLogin.disabled = true;
        try {
            renderXiaomiStatus(await apiRequest("/api/xiaomi-purifier/login", "POST"));
            if (!xiaomiLoginPolling) xiaomiLoginPolling = setInterval(loadXiaomiStatus, 1200);
        } catch (error) { xiaomiMessage.textContent = error.message; btnXiaomiLogin.disabled = false; }
    });

    btnXiaomiTest.addEventListener("click", async () => {
        btnXiaomiTest.disabled = true;
        xiaomiMessage.textContent = "Testing direct LAN connection…";
        try { renderXiaomiStatus(await apiRequest("/api/xiaomi-purifier/test", "POST")); }
        catch (error) { xiaomiMessage.textContent = error.message; }
        finally { btnXiaomiTest.disabled = false; }
    });

    async function setXiaomiPower(power) {
        const button = power ? btnXiaomiOn : btnXiaomiOff;
        button.disabled = true;
        try { renderXiaomiStatus(await apiRequest(`/api/xiaomi-purifier/${power ? "on" : "off"}`, "POST")); }
        catch (error) { xiaomiMessage.textContent = error.message; }
        finally { button.disabled = false; }
    }
    btnXiaomiOn.addEventListener("click", () => setXiaomiPower(true));
    btnXiaomiOff.addEventListener("click", () => setXiaomiPower(false));

    async function setXiaomiControl(control, payload, input = null) {
        if (input) input.disabled = true;
        try {
            renderXiaomiStatus(await apiRequest("/api/xiaomi-purifier/control", "POST", { control, ...payload }));
        } catch (error) {
            xiaomiMessage.textContent = error.message;
            await loadXiaomiStatus();
        } finally {
            if (input) input.disabled = false;
        }
    }

    xiaomiModeControl.addEventListener("change", () => setXiaomiControl("mode", { value: Number(xiaomiModeControl.value) }, xiaomiModeControl));
    xiaomiFanControl.addEventListener("change", () => setXiaomiControl("fanLevel", { value: Number(xiaomiFanControl.value) }, xiaomiFanControl));
    xiaomiPlasmaControl.addEventListener("change", () => setXiaomiControl("plasma", { enabled: xiaomiPlasmaControl.checked }, xiaomiPlasmaControl));
    xiaomiUvControl.addEventListener("change", () => setXiaomiControl("uv", { enabled: xiaomiUvControl.checked }, xiaomiUvControl));
    xiaomiChildLockControl.addEventListener("change", () => setXiaomiControl("childLock", { enabled: xiaomiChildLockControl.checked }, xiaomiChildLockControl));
    xiaomiBrightnessControl.addEventListener("change", () => setXiaomiControl("brightness", { value: Number(xiaomiBrightnessControl.value) }, xiaomiBrightnessControl));
    xiaomiFavoriteLevelControl.addEventListener("change", () => setXiaomiControl("favoriteLevel", { value: Number(xiaomiFavoriteLevelControl.value) }, xiaomiFavoriteLevelControl));
    xiaomiTemperatureUnitControl.addEventListener("change", () => setXiaomiControl("temperatureUnit", { value: Number(xiaomiTemperatureUnitControl.value) }, xiaomiTemperatureUnitControl));
    xiaomiAlarmControl.addEventListener("change", () => setXiaomiControl("alarm", { enabled: xiaomiAlarmControl.checked }, xiaomiAlarmControl));

    xiaomiAutomation.addEventListener("change", async () => {
        xiaomiAutomation.disabled = true;
        try { renderXiaomiStatus(await apiRequest("/api/xiaomi-purifier/automation", "POST", { enabled: xiaomiAutomation.checked })); }
        catch (error) { xiaomiMessage.textContent = error.message; await loadXiaomiStatus(); }
    });

    loadXiaomiStatus();
    setInterval(loadXiaomiStatus, 30000);

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
            const [data, hiddenList] = await Promise.all([
                apiRequest("/api/tapo/list"),
                apiRequest("/api/tapo/hidden")
            ]);
            tapoNodesCache = Array.isArray(data) ? data : [];
            hiddenOutlets = hiddenList;
            tapoNodesContainer.innerHTML = "";
            
            if (data.length === 0) {
                tapoNodesContainer.innerHTML = `
                    <div class="empty-state">
                        <p>No Tapo Matter devices found on the fabric.</p>
                        <p class="subtext">Pair a new device using the commissioning panel below.</p>
                    </div>
                `;
                renderShortcutsMatrix(tapoNodesCache);
                renderRoutineEditor();
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
                    ${showHiddenState ? `
                        <span class="node-id-sub">Node ID: ${node.nodeId}</span>
                    ` : ''}
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
                btn.addEventListener("click", async () => {
                    const key = btn.dataset.epKey;
                    const isCurrentlyHidden = hiddenOutlets.includes(key);
                    const newHiddenState = !isCurrentlyHidden;
                    
                    btn.disabled = true;
                    try {
                        await apiRequest("/api/tapo/hidden", "POST", { key, hidden: newHiddenState });
                        if (newHiddenState) {
                            if (!hiddenOutlets.includes(key)) hiddenOutlets.push(key);
                        } else {
                            hiddenOutlets = hiddenOutlets.filter(x => x !== key);
                        }
                        loadTapoNodes(); // Rerender
                    } catch (error) {
                        alert(`Failed to save hidden state: ${error.message}`);
                    } finally {
                        btn.disabled = false;
                    }
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
            // Refresh routine outlet selectors once the live Tapo inventory is available.
            renderRoutineEditor();

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

    // --- DYNAMIC ROUTINE BUILDER ---
    const escapeHtml = (value) => String(value ?? "")
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/\"/g, "&quot;")
        .replace(/'/g, "&#039;");

    const cloneRoutine = (routine) => JSON.parse(JSON.stringify(routine));

    function slugifyRoutine(value) {
        return String(value || "")
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, "-")
            .replace(/^-+|-+$/g, "")
            .slice(0, 80)
            .replace(/-+$/g, "");
    }

    function publicApiBase() {
        if (window.location.origin && window.location.origin !== "null") return window.location.origin;
        return `http://${window.location.host || "localhost:5000"}`;
    }

    function getTapoTargets() {
        const targets = [];
        tapoNodesCache.forEach(node => {
            (node.endpoints || []).forEach(ep => {
                targets.push({
                    nodeId: String(node.nodeId),
                    endpointId: Number(ep.endpointId),
                    label: `${node.customName || `Tapo ${node.nodeId}`} · ${ep.customName || `Outlet ${ep.endpointId}`}`
                });
            });
        });
        return targets;
    }

    function createDefaultRoutine() {
        const firstTapo = getTapoTargets()[0];
        return {
            id: null,
            name: "New routine",
            slug: "",
            stopOnError: true,
            preventDuplicateRuns: true,
            estimatedDelaySeconds: 0,
            steps: [firstTapo
                ? { type: "tapo", action: "on", nodeId: firstTapo.nodeId, endpointId: firstTapo.endpointId }
                : { type: "switchbot", action: "on" }]
        };
    }

    function routineStepTypeOptions(selected) {
        return [
            ["tapo", "Tapo outlet"],
            ["switchbot", "SwitchBot"],
            ["wol", "Wake on LAN"]
        ].map(([value, label]) => `<option value="${value}" ${selected === value ? "selected" : ""}>${label}</option>`).join("");
    }

    function routineTargetMarkup(step, index) {
        if (step.type === "tapo") {
            const selected = `${step.nodeId || ""}|${step.endpointId || ""}`;
            const options = getTapoTargets().map(target => {
                const value = `${target.nodeId}|${target.endpointId}`;
                return `<option value="${escapeHtml(value)}" ${value === selected ? "selected" : ""}>${escapeHtml(target.label)}</option>`;
            }).join("");
            return `
                <div class="form-group routine-target">
                    <label for="routine-target-${index}">Outlet</label>
                    <select id="routine-target-${index}" aria-label="Routine outlet" data-step-index="${index}" data-step-field="target">
                        ${options || `<option value="">No Tapo outlets discovered</option>`}
                    </select>
                </div>`;
        }

        const label = step.type === "wol" ? "Configured PC target" : "Configured SwitchBot Bot";
        return `
            <div class="form-group routine-target">
                <label for="routine-target-${index}">Target</label>
                <input id="routine-target-${index}" value="${label}" disabled>
            </div>`;
    }

    function routineActionMarkup(step, index) {
        if (step.type === "wol") {
            return `
                <div class="form-group routine-action">
                    <label for="routine-action-${index}">Action</label>
                    <select id="routine-action-${index}" aria-label="Routine action" disabled><option>Wake</option></select>
                </div>`;
        }

        return `
            <div class="form-group routine-action">
                <label for="routine-action-${index}">Action</label>
                <select id="routine-action-${index}" aria-label="Routine action" data-step-index="${index}" data-step-field="action">
                    <option value="on" ${step.action === "on" ? "selected" : ""}>Turn on</option>
                    <option value="off" ${step.action === "off" ? "selected" : ""}>Turn off</option>
                </select>
            </div>`;
    }

    function routineStepActions(index, count) {
        return `
            <div class="routine-step-actions">
                <button class="routine-icon-button" type="button" data-routine-move="up" data-step-index="${index}" aria-label="Move step up" ${index === 0 ? "disabled" : ""}>↑</button>
                <button class="routine-icon-button" type="button" data-routine-move="down" data-step-index="${index}" aria-label="Move step down" ${index === count - 1 ? "disabled" : ""}>↓</button>
                <button class="routine-icon-button routine-remove-button" type="button" data-routine-remove="true" data-step-index="${index}" aria-label="Remove step">×</button>
            </div>`;
    }

    function renderRoutineList() {
        routineCount.textContent = String(routines.length);
        if (routines.length === 0) {
            routineList.innerHTML = `<div class="empty-state"><p>No routines saved yet.</p><p class="subtext">Create one to get a copyable Shortcut webhook.</p></div>`;
            return;
        }

        routineList.innerHTML = routines.map(routine => {
            const delay = Number(routine.estimatedDelaySeconds || 0);
            const detail = `${(routine.steps || []).length} step${(routine.steps || []).length === 1 ? "" : "s"}${delay ? ` · ${delay}s wait` : ""}`;
            return `<button class="routine-list-item ${currentRoutine && currentRoutine.id === routine.id ? "active" : ""}" type="button" data-routine-id="${escapeHtml(routine.id)}">
                <strong>${escapeHtml(routine.name)}</strong><span>${escapeHtml(detail)}</span>
            </button>`;
        }).join("");

        routineList.querySelectorAll("[data-routine-id]").forEach(button => {
            button.addEventListener("click", () => {
                const selected = routines.find(routine => routine.id === button.dataset.routineId);
                if (!selected) return;
                routineNotice = { message: "", className: "" };
                currentRoutine = cloneRoutine(selected);
                renderRoutineList();
                renderRoutineEditor();
            });
        });
    }

    function renderRoutineEditor() {
        if (!currentRoutine) {
            routineEditor.innerHTML = `<div class="empty-state routine-editor-empty"><p>Select a saved routine or create a new one.</p></div>`;
            return;
        }

        currentRoutine.steps = Array.isArray(currentRoutine.steps) ? currentRoutine.steps : [];
        const previewSlug = currentRoutine.slug || slugifyRoutine(currentRoutine.name) || "new-routine";
        const apiPath = `/api/routines/${previewSlug}/run`;
        const apiUrl = `${publicApiBase()}${apiPath}`;
        const estimatedDelay = currentRoutine.steps
            .filter(step => step.type === "delay")
            .reduce((total, step) => total + (Number(step.seconds) || 0), 0);

        const stepsHtml = currentRoutine.steps.map((step, index) => {
            if (step.type === "delay") {
                return `<div class="routine-step routine-step-delay" data-step-index="${index}">
                    <div class="routine-step-number">${index + 1}</div>
                    <div class="form-group routine-delay-control">
                        <label for="routine-delay-${index}">Time gap</label>
                        <input id="routine-delay-${index}" aria-label="Time gap in seconds" type="number" min="1" max="3600" value="${escapeHtml(step.seconds || 1)}" data-step-index="${index}" data-step-field="seconds">
                        <span>seconds</span>
                    </div>
                    <span class="routine-step-note">Pause before the next action</span>
                    ${routineStepActions(index, currentRoutine.steps.length)}
                </div>`;
            }

            return `<div class="routine-step" data-step-index="${index}">
                <div class="routine-step-number">${index + 1}</div>
                <div class="form-group routine-device-type">
                    <label for="routine-type-${index}">Device</label>
                    <select id="routine-type-${index}" aria-label="Routine device type" data-step-index="${index}" data-step-field="type">${routineStepTypeOptions(step.type)}</select>
                </div>
                ${routineTargetMarkup(step, index)}
                ${routineActionMarkup(step, index)}
                ${routineStepActions(index, currentRoutine.steps.length)}
            </div>`;
        }).join("");

        routineEditor.innerHTML = `
            <div class="routine-editor-header">
                <div><h3>${currentRoutine.id ? "Edit routine" : "New routine"}</h3><p>Actions run from top to bottom.</p></div>
                <div class="routine-editor-actions">
                    <button class="btn btn-secondary btn-sm" type="button" id="btn-run-routine" ${currentRoutine.id ? "" : "disabled"}>▶ Run now</button>
                    <button class="btn btn-primary btn-sm" type="submit" form="routine-form">Save routine</button>
                </div>
            </div>
            <form id="routine-form" onsubmit="return false;">
                <div class="routine-field-row">
                    <div class="form-group"><label for="routine-name-input">Routine name</label><input id="routine-name-input" name="routine-name" value="${escapeHtml(currentRoutine.name)}" maxlength="80" required></div>
                    <div class="form-group"><label for="routine-slug-input">iOS Shortcut API name</label><input id="routine-slug-input" class="routine-slug-input" value="${escapeHtml(previewSlug)}" readonly></div>
                </div>
                <div class="form-group">
                    <label>Generated POST URL</label>
                    <div class="routine-api-row"><code id="routine-api-url">${escapeHtml(apiUrl)}</code><button class="btn btn-secondary btn-sm" type="button" id="btn-copy-routine-api">Copy URL</button></div>
                </div>
                <p class="routine-api-note">Paste this URL into iOS Shortcuts → Get Contents of URL. No request body is required. Use OmniGate's LAN address instead of <code>localhost</code> when the Shortcut runs on your iPhone.</p>

                <div class="routine-section-heading"><h4>Sequence</h4><span>Move steps with ↑ ↓</span></div>
                <div class="routine-steps" id="routine-steps">${stepsHtml || `<div class="empty-state"><p>Add an action or time gap to begin.</p></div>`}</div>
                <div class="routine-add-actions">
                    <button class="btn btn-secondary btn-sm" type="button" id="btn-add-routine-action">＋ Add device action</button>
                    <button class="btn btn-secondary btn-sm" type="button" id="btn-add-routine-delay">＋ Add time gap</button>
                </div>

                <div class="routine-options">
                    <label class="form-check"><input class="form-check-input" type="checkbox" id="routine-stop-on-error" ${currentRoutine.stopOnError !== false ? "checked" : ""}><span class="form-check-label">Stop if an action fails</span></label>
                    <label class="form-check"><input class="form-check-input" type="checkbox" id="routine-prevent-duplicate" ${currentRoutine.preventDuplicateRuns !== false ? "checked" : ""}><span class="form-check-label">Prevent duplicate runs</span></label>
                    <span class="routine-estimate">Estimated wait: <strong>${estimatedDelay} second${estimatedDelay === 1 ? "" : "s"}</strong></span>
                </div>
                <div class="routine-form-actions">
                    <div class="routine-primary-actions">
                        <button class="btn btn-primary" type="submit">Save routine</button>
                        <button class="btn btn-secondary" type="button" id="btn-run-routine-bottom" ${currentRoutine.id ? "" : "disabled"}>▶ Run now</button>
                    </div>
                    <button class="btn btn-secondary btn-sm" type="button" id="btn-delete-routine" ${currentRoutine.id ? "" : "disabled"}>Delete</button>
                </div>
                <div class="routine-status ${escapeHtml(routineNotice.className)}" id="routine-status" aria-live="polite">${escapeHtml(routineNotice.message)}</div>
                <div class="routine-run-progress" id="routine-run-progress" style="display:none;" aria-live="polite"></div>
            </form>`;

        const nameInput = document.getElementById("routine-name-input");
        nameInput.addEventListener("input", () => {
            currentRoutine.name = nameInput.value;
            const slugPreview = document.getElementById("routine-slug-input");
            if (!currentRoutine.id) {
                const slug = slugifyRoutine(nameInput.value) || "new-routine";
                slugPreview.value = slug;
                document.getElementById("routine-api-url").textContent = `${publicApiBase()}/api/routines/${slug}/run`;
            }
        });

        routineEditor.querySelectorAll("[data-step-field]").forEach(control => {
            control.addEventListener("change", () => updateRoutineStep(control));
            if (control.dataset.stepField === "seconds") control.addEventListener("input", () => updateRoutineStep(control, false));
        });

        routineEditor.querySelectorAll("[data-routine-move]").forEach(button => {
            button.addEventListener("click", () => moveRoutineStep(Number(button.dataset.stepIndex), button.dataset.routineMove));
        });
        routineEditor.querySelectorAll("[data-routine-remove]").forEach(button => {
            button.addEventListener("click", () => {
                currentRoutine.steps.splice(Number(button.dataset.stepIndex), 1);
                routineNotice = { message: "", className: "" };
                renderRoutineEditor();
            });
        });

        document.getElementById("btn-add-routine-action").addEventListener("click", () => {
            currentRoutine.steps.push(createDefaultRoutine().steps[0]);
            renderRoutineEditor();
        });
        document.getElementById("btn-add-routine-delay").addEventListener("click", () => {
            currentRoutine.steps.push({ type: "delay", seconds: 1 });
            renderRoutineEditor();
        });
        document.getElementById("routine-form").addEventListener("submit", saveCurrentRoutine);
        document.getElementById("btn-run-routine").addEventListener("click", runCurrentRoutine);
        document.getElementById("btn-run-routine-bottom").addEventListener("click", runCurrentRoutine);
        document.getElementById("btn-delete-routine").addEventListener("click", deleteCurrentRoutine);
        document.getElementById("btn-copy-routine-api").addEventListener("click", async () => {
            const copied = await copyToClipboard(document.getElementById("routine-api-url").textContent);
            setRoutineNotice(copied ? "Shortcut URL copied to clipboard." : "Could not copy the URL.", copied ? "success" : "danger");
        });

        document.getElementById("routine-stop-on-error").addEventListener("change", e => { currentRoutine.stopOnError = e.target.checked; });
        document.getElementById("routine-prevent-duplicate").addEventListener("change", e => { currentRoutine.preventDuplicateRuns = e.target.checked; });
    }

    function updateRoutineStep(control, rerender = true) {
        if (!currentRoutine) return;
        const index = Number(control.dataset.stepIndex);
        const step = currentRoutine.steps[index];
        if (!step) return;

        if (control.dataset.stepField === "type") {
            const type = control.value;
            if (type === "tapo") {
                const first = getTapoTargets()[0];
                currentRoutine.steps[index] = first
                    ? { type, action: "on", nodeId: first.nodeId, endpointId: first.endpointId }
                    : { type, action: "on", nodeId: "", endpointId: null };
            } else if (type === "wol") {
                currentRoutine.steps[index] = { type, action: "wake" };
            } else {
                currentRoutine.steps[index] = { type: "switchbot", action: "on" };
            }
            renderRoutineEditor();
            return;
        }

        if (control.dataset.stepField === "target") {
            const [nodeId, endpointId] = control.value.split("|");
            step.nodeId = nodeId || "";
            step.endpointId = endpointId ? Number(endpointId) : null;
        } else if (control.dataset.stepField === "seconds") {
            step.seconds = Math.max(1, Math.min(3600, Number(control.value) || 1));
            control.value = step.seconds;
            const estimated = currentRoutine.steps
                .filter(item => item.type === "delay")
                .reduce((total, item) => total + (Number(item.seconds) || 0), 0);
            const estimateLabel = routineEditor.querySelector(".routine-estimate strong");
            if (estimateLabel) estimateLabel.textContent = `${estimated} second${estimated === 1 ? "" : "s"}`;
        } else if (control.dataset.stepField === "action") {
            step.action = control.value;
        }

        if (rerender && control.dataset.stepField !== "seconds") renderRoutineEditor();
    }

    function moveRoutineStep(index, direction) {
        const targetIndex = direction === "up" ? index - 1 : index + 1;
        if (!currentRoutine || targetIndex < 0 || targetIndex >= currentRoutine.steps.length) return;
        const [step] = currentRoutine.steps.splice(index, 1);
        currentRoutine.steps.splice(targetIndex, 0, step);
        renderRoutineEditor();
    }

    function setRoutineNotice(message, className = "") {
        routineNotice = { message, className };
        const status = document.getElementById("routine-status");
        if (status) {
            status.textContent = message;
            status.className = `routine-status ${className}`;
        }
    }

    function routinePayloadFromCurrent() {
        return {
            name: currentRoutine.name,
            stopOnError: currentRoutine.stopOnError !== false,
            preventDuplicateRuns: currentRoutine.preventDuplicateRuns !== false,
            steps: currentRoutine.steps.map(step => ({
                type: step.type,
                action: step.type === "delay" ? null : step.action,
                nodeId: step.type === "tapo" ? step.nodeId : null,
                endpointId: step.type === "tapo" ? step.endpointId : null,
                seconds: step.type === "delay" ? Number(step.seconds) : null,
                macAddress: step.type === "wol" ? (step.macAddress || null) : null,
                broadcastIp: step.type === "wol" ? (step.broadcastIp || null) : null,
                port: step.type === "wol" ? (step.port || null) : null
            }))
        };
    }

    async function saveCurrentRoutine(event) {
        if (event) event.preventDefault();
        if (!currentRoutine) return;
        currentRoutine.name = document.getElementById("routine-name-input").value.trim();
        currentRoutine.stopOnError = document.getElementById("routine-stop-on-error").checked;
        currentRoutine.preventDuplicateRuns = document.getElementById("routine-prevent-duplicate").checked;
        if (!currentRoutine.name) {
            setRoutineNotice("Routine name is required.", "danger");
            return;
        }
        if (!currentRoutine.steps.length) {
            setRoutineNotice("Add at least one action or time gap.", "danger");
            return;
        }

        try {
            const method = currentRoutine.id ? "PUT" : "POST";
            const endpoint = currentRoutine.id ? `/api/routines/${encodeURIComponent(currentRoutine.id)}` : "/api/routines";
            const saved = await apiRequest(endpoint, method, routinePayloadFromCurrent());
            const index = routines.findIndex(routine => routine.id === saved.id);
            if (index >= 0) routines[index] = saved;
            else routines.push(saved);
            currentRoutine = cloneRoutine(saved);
            routineNotice = { message: "Routine saved. The POST URL is ready for iOS Shortcuts.", className: "success" };
            renderRoutineList();
            renderRoutineEditor();
            renderShortcutsMatrix(tapoNodesCache);
        } catch (error) {
            setRoutineNotice(`Could not save routine: ${error.message}`, "danger");
        }
    }

    async function runCurrentRoutine() {
        if (!currentRoutine || !currentRoutine.id) {
            setRoutineNotice("Save the routine before running it.", "danger");
            return;
        }
        try {
            const result = await apiRequest(`/api/routines/${encodeURIComponent(currentRoutine.id)}/run`, "POST");
            setRoutineNotice(`Routine accepted. Run ID: ${result.runId}`, "success");
            pollRoutineRun(result.runId);
        } catch (error) {
            setRoutineNotice(`Could not run routine: ${error.message}`, "danger");
        }
    }

    async function pollRoutineRun(runId) {
        if (routinePollTimer) window.clearTimeout(routinePollTimer);
        try {
            const run = await apiRequest(`/api/routine-runs/${encodeURIComponent(runId)}`);
            const progress = document.getElementById("routine-run-progress");
            if (progress) {
                progress.style.display = "block";
                const stepText = run.totalSteps ? `Step ${Math.min(run.currentStep || 0, run.totalSteps)} of ${run.totalSteps}` : "Preparing";
                progress.innerHTML = `<strong>${escapeHtml(run.status)}</strong> · ${escapeHtml(stepText)}${run.error ? ` · ${escapeHtml(run.error)}` : ""}`;
            }

            if (["Completed", "CompletedWithErrors", "Failed", "Cancelled"].includes(run.status)) {
                const className = run.status === "Completed" ? "success" : "danger";
                setRoutineNotice(`Run ${run.status.toLowerCase()}.`, className);
                return;
            }

            routinePollTimer = window.setTimeout(() => pollRoutineRun(runId), 900);
        } catch (error) {
            setRoutineNotice(`Run status unavailable: ${error.message}`, "danger");
        }
    }

    async function deleteCurrentRoutine() {
        if (!currentRoutine || !currentRoutine.id || !confirm(`Delete routine “${currentRoutine.name}”?`)) return;
        try {
            await apiRequest(`/api/routines/${encodeURIComponent(currentRoutine.id)}`, "DELETE");
            routines = routines.filter(routine => routine.id !== currentRoutine.id);
            currentRoutine = routines.length ? cloneRoutine(routines[0]) : null;
            routineNotice = { message: "", className: "" };
            renderRoutineList();
            renderRoutineEditor();
            renderShortcutsMatrix(tapoNodesCache);
        } catch (error) {
            setRoutineNotice(`Could not delete routine: ${error.message}`, "danger");
        }
    }

    async function loadRoutines() {
        try {
            const data = await apiRequest("/api/routines");
            routines = Array.isArray(data) ? data : [];
            if (currentRoutine && currentRoutine.id) {
                const refreshed = routines.find(routine => routine.id === currentRoutine.id);
                if (refreshed) currentRoutine = cloneRoutine(refreshed);
            } else if (!currentRoutine && routines.length) {
                currentRoutine = cloneRoutine(routines[0]);
            }
            renderRoutineList();
            renderRoutineEditor();
            renderShortcutsMatrix(tapoNodesCache);
        } catch (error) {
            routineList.innerHTML = `<div class="alert-box danger">Could not load routines: ${escapeHtml(error.message)}</div>`;
        }
    }

    btnToggleRoutines.addEventListener("click", () => {
        const isHidden = routinesPanel.style.display === "none";
        routinesPanel.style.display = isHidden ? "block" : "none";
        btnToggleRoutines.classList.toggle("btn-primary", isHidden);
        btnToggleRoutines.classList.toggle("btn-secondary", !isHidden);
        if (isHidden) loadRoutines();
    });
    btnRefreshRoutines.addEventListener("click", loadRoutines);
    btnNewRoutine.addEventListener("click", () => {
        routineNotice = { message: "", className: "" };
        currentRoutine = createDefaultRoutine();
        renderRoutineList();
        renderRoutineEditor();
    });

    loadTapoNodes(); // Initial trigger
    loadRoutines();

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


    // --- WAKE ON LAN CONTROLLER ---
    const btnTriggerWol = document.getElementById("btn-trigger-wol");
    const wolMacInput = document.getElementById("wol-mac-input");
    const wolFeedback = document.getElementById("wol-feedback");

    btnTriggerWol.addEventListener("click", async () => {
        btnTriggerWol.disabled = true;
        wolFeedback.style.display = "none";
        
        const macAddress = wolMacInput.value.trim() || null;
        
        try {
            const res = await apiRequest("/api/wol/wake", "POST", { macAddress });
            wolFeedback.style.display = "block";
            wolFeedback.className = "alert-box success";
            wolFeedback.innerHTML = `<strong>Sent!</strong> ${res.message}`;
        } catch (error) {
            wolFeedback.style.display = "block";
            wolFeedback.className = "alert-box danger";
            wolFeedback.innerHTML = `<strong>Error:</strong> ${error.message}`;
        } finally {
            btnTriggerWol.disabled = false;
        }
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
        const baseUrl = publicApiBase();
        
        let html = "";
        
        // 1. SwitchBot Bot
        html += `
            <tr>
                <td data-label="Device / Outlet"><strong>SwitchBot Bot</strong><br><span style="font-size:0.75rem; color:var(--text-muted);">Direct BLE Control</span></td>
                <td data-label="ON / Run URL"><span class="shortcut-url-code" title="Click to copy">${baseUrl}/api/switchbot/on</span></td>
                <td data-label="Trigger OFF URL"><span class="shortcut-url-code" title="Click to copy">${baseUrl}/api/switchbot/off</span></td>
                <td data-label="Toggle State URL"><span class="shortcut-url-code" style="color: var(--text-muted); cursor: not-allowed; background: none; border: none;">[N/A]</span></td>
            </tr>
        `;
        
        // 1a. Wake on LAN
        html += `
            <tr>
                <td data-label="Device / Outlet"><strong>Wake on LAN (WOL)</strong><br><span style="font-size:0.75rem; color:var(--text-muted);">Broadcast UDP Magic Packet</span></td>
                <td data-label="ON / Run URL"><span class="shortcut-url-code" title="Click to copy">${baseUrl}/api/wol/wake</span></td>
                <td data-label="Trigger OFF URL"><span class="shortcut-url-code" style="color: var(--text-muted); cursor: not-allowed; background: none; border: none;">[N/A]</span></td>
                <td data-label="Toggle State URL"><span class="shortcut-url-code" style="color: var(--text-muted); cursor: not-allowed; background: none; border: none;">[N/A]</span></td>
            </tr>
        `;
        
        // 2. Tapo Outlets
        tapoNodes.forEach(node => {
            node.endpoints.forEach(ep => {
                const epKey = `${node.nodeId}_${ep.endpointId}`;
                const isEpHidden = hiddenOutlets.includes(epKey);

                const isSafetyLocked = false;
                const onUrl = `${baseUrl}/api/tapo/${node.nodeId}/${ep.endpointId}/on`;
                const offUrl = `${baseUrl}/api/tapo/${node.nodeId}/${ep.endpointId}/off`;
                const toggleUrl = `${baseUrl}/api/tapo/${node.nodeId}/${ep.endpointId}/toggle`;
                
                html += `
                    <tr class="${isEpHidden ? 'faded-row' : ''}">
                        <td data-label="Device / Outlet"><strong>${ep.customName}</strong><br><span style="font-size:0.75rem; color:var(--text-muted);">${node.customName} &bull; EP ${ep.endpointId}</span></td>
                        <td data-label="ON / Run URL"><span class="shortcut-url-code" title="Click to copy">${onUrl}</span></td>
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

        // Saved routines use the same POST action in iOS Shortcuts, but one
        // webhook now performs the complete sequence and its time gaps.
        routines.forEach(routine => {
            const routineUrl = `${baseUrl}${routine.apiPath || `/api/routines/${routine.slug}/run`}`;
            const delay = Number(routine.estimatedDelaySeconds || 0);
            const detail = `${(routine.steps || []).length} step${(routine.steps || []).length === 1 ? "" : "s"}${delay ? ` · ${delay}s wait` : ""}`;
            html += `
                <tr>
                    <td data-label="Device / Outlet"><strong>${escapeHtml(routine.name)}</strong><br><span style="font-size:0.75rem; color:var(--text-muted);">Routine webhook · ${escapeHtml(detail)}</span></td>
                    <td data-label="ON / Run URL"><span class="shortcut-url-code" title="Click to copy">${escapeHtml(routineUrl)}</span></td>
                    <td data-label="Trigger OFF URL"><span class="shortcut-url-code" style="color: var(--text-muted); cursor: not-allowed; background: none; border: none;">[N/A]</span></td>
                    <td data-label="Toggle State URL"><span class="shortcut-url-code" style="color: var(--text-muted); cursor: not-allowed; background: none; border: none;">[N/A]</span></td>
                </tr>
            `;
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
