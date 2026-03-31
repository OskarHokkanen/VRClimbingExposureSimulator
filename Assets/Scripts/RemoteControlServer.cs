using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Runs a lightweight HTTP server on the Quest headset.
/// Connect from any device on the same Wi-Fi network by visiting:
///   http://[quest-ip]:8080
///
/// Serves a mobile-friendly control page with sliders and presets
/// for wall height, wing angles, wing width, and environment selection.
///
/// SETUP:
///   1. Add this component to your CalibrationSystem
///   2. Wire references to EnvironmentManager, SimpleWallSystem, EnvironmentSwitcher
///   3. Build and deploy to Quest
///   4. On Quest, go to Settings → Wi-Fi → check the IP address
///   5. On your phone (same Wi-Fi), open browser → http://[quest-ip]:8080
///
/// ANDROID PERMISSION:
///   Requires internet permission. Unity usually includes this by default.
///   If not, add to AndroidManifest:
///     <uses-permission android:name="android.permission.INTERNET" />
/// </summary>
public class RemoteControlServer : MonoBehaviour
{
    [Header("References")]
    public SimpleWallSystem wallSystem;
    public EnvironmentManager environmentManager;
    public EnvironmentSwitcher environmentSwitcher;

    [Header("Server Settings")]
    [Tooltip("Port to listen on")]
    public int port = 8080;

    [Tooltip("Show the IP address in the VR status text")]
    public bool showIPInVR = true;
    public TMPro.TextMeshProUGUI statusText;
    
    [Header("Presets")]
    public List<WallPreset> presets = new List<WallPreset>();

    private HttpListener _listener;
    private Thread _serverThread;
    private bool _running;

    // Queued actions to execute on main thread
    private readonly Queue<Action> _mainThreadQueue = new Queue<Action>();
    private readonly object _queueLock = new object();

    // Cached state for the web UI
    private string _localIP = "unknown";

    public string LocalIP => _localIP;
    public string URL => $"http://{_localIP}:{port}";

    // ────────────────────────────────────────────
    // Lifecycle
    // ────────────────────────────────────────────

    void Start()
    {
        _localIP = GetLocalIP();
        Debug.Log($"RemoteControlServer: starting on {URL}");
        StartServer();
    }

    void Update()
    {
        // Process queued actions on main thread
        lock (_queueLock)
        {
            while (_mainThreadQueue.Count > 0)
            {
                _mainThreadQueue.Dequeue()?.Invoke();
            }
        }
    }

    void OnDestroy()
    {
        StopServer();
    }

    void OnApplicationQuit()
    {
        StopServer();
    }

    // ────────────────────────────────────────────
    // Server
    // ────────────────────────────────────────────

    void StartServer()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://*:{port}/");

        try
        {
            _listener.Start();
            _running = true;
            _serverThread = new Thread(ListenLoop) { IsBackground = true };
            _serverThread.Start();
            Debug.Log($"RemoteControlServer: listening on port {port}");
            if (statusText != null)
            {
                statusText.text = URL;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"RemoteControlServer: failed to start: {e.Message}");
        }
    }

    void StopServer()
    {
        _running = false;
        if (_listener != null && _listener.IsListening)
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    void ListenLoop()
    {
        while (_running && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = _listener.GetContext();
                ProcessRequest(context);
            }
            catch (HttpListenerException)
            {
                // Listener was stopped
                break;
            }
            catch (Exception e)
            {
                Debug.LogError($"RemoteControlServer: {e.Message}");
            }
        }
    }

    // ────────────────────────────────────────────
    // Request Handling
    // ────────────────────────────────────────────

    void ProcessRequest(HttpListenerContext context)
    {
        string path = context.Request.Url.AbsolutePath;
        string method = context.Request.HttpMethod;

        try
        {
            if (path == "/" || path == "/index.html")
            {
                ServeHTML(context.Response);
            }
            else if (path == "/api/state" && method == "GET")
            {
                ServeState(context.Response);
            }
            else if (path == "/api/set" && method == "POST")
            {
                HandleSet(context.Request, context.Response);
            }
            else if (path == "/api/preset" && method == "POST")
            {
                HandlePreset(context.Request, context.Response);
            }
            else if (path == "/api/reset" && method == "POST")
            {
                HandleReset(context.Response);
            }
            else
            {
                context.Response.StatusCode = 404;
                WriteResponse(context.Response, "Not found");
            }
        }
        catch (Exception e)
        {
            context.Response.StatusCode = 500;
            WriteResponse(context.Response, e.Message);
        }
    }

    void ServeState(HttpListenerResponse response)
    {
        float wallHeight = 5f;
        float leftWing = 0f, rightWing = 0f;
        float wingW = 1.5f, wallW = 3f;
        int envIndex = 0;
        int envCount = 0;
        string envName = "";

        // Read values (thread-safe reads of floats are atomic on most platforms)
        if (environmentManager != null) wallHeight = environmentManager.wallHeight;
        if (wallSystem != null)
        {
            leftWing = wallSystem.leftWingAngle;
            rightWing = wallSystem.rightWingAngle;
            wingW = wallSystem.wingWidth;
            wallW = wallSystem.wallWidth;
        }
        if (environmentSwitcher != null)
        {
            envIndex = environmentSwitcher.ActiveIndex;
            envCount = environmentSwitcher.environments.Count;
            envName = environmentSwitcher.ActiveEnvironment?.displayName ?? "";
        }

        // Build preset list
        var presetJson = new StringBuilder("[");
        for (int i = 0; i < presets.Count; i++)
        {
            if (i > 0) presetJson.Append(",");
            var p = presets[i];
            presetJson.Append($"{{\"name\":\"{EscapeJson(p.name)}\"," +
                $"\"height\":{p.wallHeight:F1}," +
                $"\"leftWing\":{p.leftWingAngle:F0}," +
                $"\"rightWing\":{p.rightWingAngle:F0}," +
                $"\"wingWidth\":{p.wingWidth:F1}," +
                $"\"wallWidth\":{p.wallWidth:F1}," +
                $"\"envIndex\":{p.environmentIndex}}}");
        }
        presetJson.Append("]");

        // Build environment list
        var envListJson = new StringBuilder("[");
        if (environmentSwitcher != null)
        {
            for (int i = 0; i < environmentSwitcher.environments.Count; i++)
            {
                if (i > 0) envListJson.Append(",");
                string name = environmentSwitcher.environments[i].displayName;
                envListJson.Append($"\"{EscapeJson(name)}\"");
            }
        }
        envListJson.Append("]");

        string phase = wallSystem != null ? wallSystem.CurrentPhase.ToString() : "Unknown";

        string json = $"{{" +
            $"\"wallHeight\":{wallHeight:F1}," +
            $"\"leftWingAngle\":{leftWing:F1}," +
            $"\"rightWingAngle\":{rightWing:F1}," +
            $"\"wingWidth\":{wingW:F1}," +
            $"\"wallWidth\":{wallW:F1}," +
            $"\"envIndex\":{envIndex}," +
            $"\"envName\":\"{EscapeJson(envName)}\"," +
            $"\"envCount\":{envCount}," +
            $"\"phase\":\"{phase}\"," +
            $"\"environments\":{envListJson}," +
            $"\"presets\":{presetJson}" +
            $"}}";

        response.ContentType = "application/json";
        WriteResponse(response, json);
    }

    void HandleSet(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        // Simple key=value parsing from JSON-like body
        // Expected: {"key":"wallHeight","value":10.0}
        string key = ExtractJsonString(body, "key");
        float value = ExtractJsonFloat(body, "value");

        RunOnMainThread(() =>
        {
            switch (key)
            {
                case "wallHeight":
                    if (environmentManager != null)
                        environmentManager.SetWallHeight(value);
                    if (wallSystem != null && wallSystem.IsCalibrated)
                        wallSystem.RebuildMeshes();
                    break;
                case "leftWingAngle":
                    if (wallSystem != null)
                    {
                        wallSystem.leftWingAngle = Mathf.Clamp(value, -135f, 135f);
                        if (wallSystem.IsCalibrated) wallSystem.RebuildMeshes();
                    }
                    break;
                case "rightWingAngle":
                    if (wallSystem != null)
                    {
                        wallSystem.rightWingAngle = Mathf.Clamp(value, -135f, 135f);
                        if (wallSystem.IsCalibrated) wallSystem.RebuildMeshes();
                    }
                    break;
                case "wingWidth":
                    if (wallSystem != null)
                    {
                        wallSystem.wingWidth = Mathf.Clamp(value, 0.2f, 5f);
                        if (wallSystem.IsCalibrated) wallSystem.RebuildMeshes();
                    }
                    break;
                case "wallWidth":
                    if (wallSystem != null)
                    {
                        wallSystem.wallWidth = Mathf.Clamp(value, 1f, 15f);
                        if (wallSystem.IsCalibrated) wallSystem.RebuildMeshes();
                    }
                    break;
                case "environment":
                    if (environmentSwitcher != null)
                        environmentSwitcher.SwitchTo((int)value);
                    break;
            }
        });

        response.ContentType = "application/json";
        WriteResponse(response, "{\"ok\":true}");
    }

    void HandlePreset(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        int index = (int)ExtractJsonFloat(body, "index");

        if (index >= 0 && index < presets.Count)
        {
            var p = presets[index];
            RunOnMainThread(() =>
            {
                if (environmentManager != null)
                    environmentManager.SetWallHeight(p.wallHeight);
                if (wallSystem != null)
                {
                    wallSystem.wallWidth = p.wallWidth;
                    wallSystem.leftWingAngle = p.leftWingAngle;
                    wallSystem.rightWingAngle = p.rightWingAngle;
                    wallSystem.wingWidth = p.wingWidth;
                    if (wallSystem.IsCalibrated) wallSystem.RebuildMeshes();
                }
                if (environmentSwitcher != null && p.environmentIndex >= 0)
                    environmentSwitcher.SwitchTo(p.environmentIndex);
            });
        }

        response.ContentType = "application/json";
        WriteResponse(response, "{\"ok\":true}");
    }

    void HandleReset(HttpListenerResponse response)
    {
        RunOnMainThread(() =>
        {
            if (wallSystem != null)
                wallSystem.ResetCalibration();
        });

        response.ContentType = "application/json";
        WriteResponse(response, "{\"ok\":true}");
    }

    // ────────────────────────────────────────────
    // HTML Page
    // ────────────────────────────────────────────

    void ServeHTML(HttpListenerResponse response)
    {
        response.ContentType = "text/html; charset=utf-8";
        WriteResponse(response, GetControlPageHTML());
    }

    string GetControlPageHTML()
    {
        return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0, user-scalable=no"">
<title>Climbing Wall VR Control</title>
<style>
* { box-sizing: border-box; margin: 0; padding: 0; }
body {
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
  background: #1a1a2e; color: #eee;
  padding: 16px; max-width: 500px; margin: 0 auto;
}
h1 { font-size: 20px; font-weight: 600; margin-bottom: 16px; color: #64ffda; }
h2 { font-size: 14px; font-weight: 500; color: #888; text-transform: uppercase;
     letter-spacing: 1px; margin: 20px 0 8px; }
.card {
  background: #16213e; border-radius: 12px; padding: 16px; margin-bottom: 12px;
}
.slider-row {
  display: flex; align-items: center; gap: 10px; margin: 8px 0;
}
.slider-row label {
  min-width: 100px; font-size: 14px; color: #aaa;
}
.slider-row input[type=range] {
  flex: 1; accent-color: #64ffda; height: 32px;
}
.slider-row .val {
  min-width: 50px; text-align: right; font-size: 14px;
  font-weight: 600; color: #64ffda;
}
.presets { display: flex; flex-wrap: wrap; gap: 8px; }
.preset-btn {
  background: #0f3460; border: 1px solid #1a5276; border-radius: 8px;
  color: #eee; padding: 10px 16px; font-size: 14px; cursor: pointer;
  flex: 1; min-width: 120px; text-align: center;
  transition: background 0.15s;
}
.preset-btn:active { background: #1a5276; }
.preset-btn.active { background: #1a5276; border-color: #64ffda; color: #64ffda; }
.env-grid { display: flex; flex-wrap: wrap; gap: 8px; }
.env-btn {
  background: #0f3460; border: 1px solid #1a5276; border-radius: 8px;
  color: #eee; padding: 10px 16px; font-size: 14px; cursor: pointer;
  flex: 1; min-width: 100px; text-align: center;
  transition: background 0.15s;
}
.env-btn:active { background: #1a5276; }
.env-btn.active { background: #1a5276; border-color: #64ffda; color: #64ffda; }
.status { font-size: 12px; color: #555; text-align: center; margin-top: 16px; }
.phase-bar {
  display: flex; align-items: center; justify-content: space-between;
  background: #16213e; border-radius: 12px; padding: 12px 16px; margin-bottom: 12px;
}
.phase-label { font-size: 14px; color: #aaa; }
.phase-value { font-size: 14px; font-weight: 600; color: #64ffda; }
.reset-btn {
  background: #5c1a1a; border: 1px solid #8b2e2e; border-radius: 8px;
  color: #ff6b6b; padding: 10px 20px; font-size: 14px; font-weight: 600;
  cursor: pointer; width: 100%; margin-top: 4px;
  transition: background 0.15s;
}
.reset-btn:active { background: #8b2e2e; }
</style>
</head>
<body>

<h1>Climbing Wall VR</h1>

<div class=""phase-bar"">
  <span class=""phase-label"">Status</span>
  <span class=""phase-value"" id=""phase"">--</span>
</div>

<div class=""card"">
  <h2>Wall Height</h2>
  <div class=""slider-row"">
    <label>Height</label>
    <input type=""range"" id=""wallHeight"" min=""2"" max=""50"" step=""0.5"" value=""5"">
    <span class=""val"" id=""wallHeight-val"">5.0m</span>
  </div>
</div>

<div class=""card"">
  <h2>Wall Width</h2>
  <div class=""slider-row"">
    <label>Width</label>
    <input type=""range"" id=""wallWidth"" min=""1"" max=""15"" step=""0.5"" value=""3"">
    <span class=""val"" id=""wallWidth-val"">3.0m</span>
  </div>
</div>

<div class=""card"">
  <h2>Wings</h2>
  <div class=""slider-row"">
    <label>Left angle</label>
    <input type=""range"" id=""leftWingAngle"" min=""-135"" max=""135"" step=""1"" value=""0"">
    <span class=""val"" id=""leftWingAngle-val"">0°</span>
  </div>
  <div class=""slider-row"">
    <label>Right angle</label>
    <input type=""range"" id=""rightWingAngle"" min=""-135"" max=""135"" step=""1"" value=""0"">
    <span class=""val"" id=""rightWingAngle-val"">0°</span>
  </div>
  <div class=""slider-row"">
    <label>Wing width</label>
    <input type=""range"" id=""wingWidth"" min=""0.2"" max=""5"" step=""0.1"" value=""1.5"">
    <span class=""val"" id=""wingWidth-val"">1.5m</span>
  </div>
</div>

<div class=""card"">
  <h2>Environment</h2>
  <div class=""env-grid"" id=""env-grid""></div>
</div>

<div class=""card"">
  <h2>Presets</h2>
  <div class=""presets"" id=""presets""></div>
</div>

<div class=""card"">
  <button class=""reset-btn"" onclick=""resetWalls()"">Reset Wall Placement</button>
</div>

<div class=""status"" id=""status"">Connecting...</div>

<script>
const API = '';
let state = {};
let debounceTimers = {};

async function fetchState() {
  try {
    const r = await fetch(API + '/api/state');
    state = await r.json();
    updateUI();
    document.getElementById('status').textContent = 'Connected';
  } catch(e) {
    document.getElementById('status').textContent = 'Disconnected — retrying...';
  }
}

function updateUI() {
  setSlider('wallHeight', state.wallHeight, v => v.toFixed(1) + 'm');
  setSlider('wallWidth', state.wallWidth, v => v.toFixed(1) + 'm');
  setSlider('leftWingAngle', state.leftWingAngle, v => v.toFixed(0) + '°');
  setSlider('rightWingAngle', state.rightWingAngle, v => v.toFixed(0) + '°');
  setSlider('wingWidth', state.wingWidth, v => v.toFixed(1) + 'm');
  document.getElementById('phase').textContent = state.phase || '--';
  buildEnvButtons();
  buildPresetButtons();
}

function setSlider(id, value, fmt) {
  const el = document.getElementById(id);
  if (el && document.activeElement !== el) {
    el.value = value;
  }
  const valEl = document.getElementById(id + '-val');
  if (valEl) valEl.textContent = fmt(parseFloat(value));
}

function buildEnvButtons() {
  const grid = document.getElementById('env-grid');
  if (!state.environments || state.environments.length === 0) {
    grid.innerHTML = '<span style=""color:#555"">No environments</span>';
    return;
  }
  grid.innerHTML = '';
  state.environments.forEach((name, i) => {
    const btn = document.createElement('button');
    btn.className = 'env-btn' + (i === state.envIndex ? ' active' : '');
    btn.textContent = name;
    btn.onclick = () => sendSet('environment', i);
    grid.appendChild(btn);
  });
}

function buildPresetButtons() {
  const container = document.getElementById('presets');
  if (!state.presets || state.presets.length === 0) {
    container.innerHTML = '<span style=""color:#555"">No presets configured</span>';
    return;
  }
  container.innerHTML = '';
  state.presets.forEach((p, i) => {
    const btn = document.createElement('button');
    btn.className = 'preset-btn';
    btn.textContent = p.name;
    btn.onclick = () => applyPreset(i);
    container.appendChild(btn);
  });
}

function sendSet(key, value) {
  fetch(API + '/api/set', {
    method: 'POST',
    headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({key, value: parseFloat(value)})
  }).then(() => setTimeout(fetchState, 200));
}

function sendSetDebounced(key, value) {
  clearTimeout(debounceTimers[key]);
  debounceTimers[key] = setTimeout(() => sendSet(key, value), 80);
}

function applyPreset(index) {
  fetch(API + '/api/preset', {
    method: 'POST',
    headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({index})
  }).then(() => setTimeout(fetchState, 300));
}

function resetWalls() {
  if (confirm('Reset wall placement? You will need to recalibrate.')) {
    fetch(API + '/api/reset', { method: 'POST' })
      .then(() => setTimeout(fetchState, 500));
  }
}

// Wire up sliders
['wallHeight','wallWidth','leftWingAngle','rightWingAngle','wingWidth'].forEach(id => {
  const el = document.getElementById(id);
  const valEl = document.getElementById(id + '-val');
  const fmts = {
    wallHeight: v => v.toFixed(1) + 'm',
    wallWidth: v => v.toFixed(1) + 'm',
    leftWingAngle: v => v.toFixed(0) + '°',
    rightWingAngle: v => v.toFixed(0) + '°',
    wingWidth: v => v.toFixed(1) + 'm',
  };
  el.addEventListener('input', () => {
    valEl.textContent = fmts[id](parseFloat(el.value));
    sendSetDebounced(id, el.value);
  });
});

// Poll state
fetchState();
setInterval(fetchState, 2000);
</script>
</body>
</html>";
    }

    // ────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────

    void RunOnMainThread(Action action)
    {
        lock (_queueLock)
        {
            _mainThreadQueue.Enqueue(action);
        }
    }

    void WriteResponse(HttpListenerResponse response, string content)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(content);
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.OutputStream.Close();
    }

    string GetLocalIP()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        string ip = addr.Address.ToString();
                        if (!ip.StartsWith("127.")) return ip;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"RemoteControlServer: could not detect IP: {e.Message}");
        }
        return "0.0.0.0";
    }

    // Simple JSON helpers (avoiding dependency on JsonUtility for these)
    string ExtractJsonString(string json, string key)
    {
        string search = $"\"{key}\":\"";
        int start = json.IndexOf(search);
        if (start < 0) return "";
        start += search.Length;
        int end = json.IndexOf("\"", start);
        return end > start ? json.Substring(start, end - start) : "";
    }

    float ExtractJsonFloat(string json, string key)
    {
        string search = $"\"{key}\":";
        int start = json.IndexOf(search);
        if (start < 0) return 0;
        start += search.Length;
        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-'))
            end++;
        float.TryParse(json.Substring(start, end - start),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float result);
        return result;
    }

    string EscapeJson(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
}

/// <summary>
/// A saved preset with all wall/environment settings.
/// Configure these in the Inspector on RemoteControlServer.
/// </summary>
[System.Serializable]
public class WallPreset
{
    public string name = "Preset";
    public float wallHeight = 5f;
    public float wallWidth = 3f;
    public float leftWingAngle = 0f;
    public float rightWingAngle = 0f;
    public float wingWidth = 1.5f;
    public int environmentIndex = 0;
}