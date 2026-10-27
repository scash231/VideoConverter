namespace VideoConverterUI.Services;

/// <summary>
/// Owns the active media engine (Legacy = Python, Native = C#, Ferrite = Rust) and hands the UI a
/// stable <see cref="IEngineService"/> proxy. Every call through the proxy pins the engine it started
/// on, so switching engines while a download, upload, subtitle or metadata call runs is deferred until
/// that engine is idle instead of cancelling the work.
/// </summary>
public static class EngineProvider
{
    public const string NativeKey = "native";
    public const string ClassicKey = "python";
    public const string FerriteKey = "ferrite";

    private static IEngineService? _currentEngine;
    private static string _currentKey = NativeKey;
    private static string? _pendingKey;
    private static BackendLauncher? _launcher;
    private static readonly object _lock = new();
    private static readonly object _launcherLock = new();
    private static readonly Dictionary<IEngineService, int> _busy = new(ReferenceEqualityComparer.Instance);
    private static readonly EngineProxy _proxy = new();

    public static event Action<string>? OutputReceived;
    public static event Action? EngineChanged;

    /// <summary>Raised (with the target engine key) when a switch waits for running work.</summary>
    public static event Action<string>? EngineSwitchDeferred;

    /// <summary>The engine every window talks to. Stays valid across engine switches.</summary>
    public static IEngineService Current => _proxy;

    /// <summary>Settings value of the engine that is running right now (native / python / ferrite).</summary>
    public static string ActiveKey
    {
        get
        {
            lock (_lock)
                return _currentEngine != null ? _currentKey : Normalize(SettingsService.Current.ExecutionEngine);
        }
    }

    /// <summary>The engine a deferred switch will change to, if any.</summary>
    public static string? PendingKey
    {
        get { lock (_lock) return _pendingKey; }
    }

    public static bool IsNative => ActiveKey == NativeKey;

    /// <summary>Unknown or missing values fall back to Native, like before Ferrite existed.</summary>
    public static string Normalize(string? key)
    {
        if (string.Equals(key, ClassicKey, StringComparison.OrdinalIgnoreCase)) return ClassicKey;
        if (string.Equals(key, FerriteKey, StringComparison.OrdinalIgnoreCase)) return FerriteKey;
        return NativeKey;
    }

    /// <summary>Short product name of an engine ("Native", "Legacy", "Ferrite").</summary>
    public static string DisplayName(string? key) => Normalize(key) switch
    {
        ClassicKey => "Legacy",
        FerriteKey => "Ferrite",
        _ => "Native",
    };

    public static async Task<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (ActiveKey == ClassicKey)
        {
            // Locating Python and freeing the port spawns several processes – keep that off the UI thread.
            await Task.Run(StartPythonBackend, ct);
            return await Current.WaitForHealthAsync(ct, SettingsService.Current.BackendTimeoutSeconds);
        }

        // Native and Ferrite resolve yt-dlp/ffmpeg in the background on first use.
        var ok = await Current.WaitForHealthAsync(ct);
        if (!ok && Peek() is IEngineDiagnostics { LastStartError: { } error })
            throw new Exception(error);
        return ok;
    }

    /// <summary>
    /// Switches to <paramref name="newMode"/> and saves it. Returns false when the switch was deferred
    /// because the active engine is still working; it is applied automatically once that engine is idle.
    /// </summary>
    public static bool SwitchEngine(string newMode)
    {
        var key = Normalize(newMode);
        SettingsService.Current.ExecutionEngine = key;
        SettingsService.Save(SettingsService.Current);

        bool applied;
        lock (_lock)
        {
            if (_currentEngine != null && _currentKey == key)
            {
                _pendingKey = null;
                return true;
            }

            if (_currentEngine != null && _busy.ContainsKey(_currentEngine))
            {
                _pendingKey = key;
                applied = false;
            }
            else
            {
                _pendingKey = null;
                ReplaceEngineLocked(key);
                applied = true;
            }
        }

        if (applied)
            AfterSwitch();
        else
            EngineSwitchDeferred?.Invoke(key);
        return applied;
    }

    private static void ReplaceEngineLocked(string key)
    {
        _currentEngine?.Dispose();
        _currentEngine = CreateEngine(key);
        _currentKey = key;
    }

    private static void AfterSwitch()
    {
        if (ActiveKey == ClassicKey)
            _ = Task.Run(StartPythonBackend);
        else
            StopPythonBackend();

        EngineChanged?.Invoke();
    }

    public static async Task<bool> RestartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _currentEngine?.Dispose();
            _currentEngine = null;
            _pendingKey = null;
        }
        StopPythonBackend();
        await Task.Delay(600, ct);
        return await InitializeAsync(ct);
    }

    public static void SendInput(string text)
    {
        _launcher?.SendInput(text);
    }

    public static void EmitOutput(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            OutputReceived?.Invoke(line);
        }
    }

    // ── Leases: which engine is busy ─────────────────────────────────────────

    /// <summary>True while any engine has a call or job running (a download, upload, metadata fetch…).</summary>
    public static bool IsBusy
    {
        get { lock (_lock) return _busy.Count > 0; }
    }

    private static IEngineService EnsureEngineLocked()
    {
        if (_currentEngine == null)
        {
            _currentKey = Normalize(SettingsService.Current.ExecutionEngine);
            _currentEngine = CreateEngine(_currentKey);
        }
        return _currentEngine;
    }

    /// <summary>The active engine, without pinning it (name lookups).</summary>
    internal static IEngineService Peek()
    {
        lock (_lock)
            return EnsureEngineLocked();
    }

    /// <summary>Pins the active engine until <see cref="Release"/> is called.</summary>
    internal static IEngineService Acquire()
    {
        lock (_lock)
        {
            var engine = EnsureEngineLocked();
            _busy[engine] = _busy.TryGetValue(engine, out var n) ? n + 1 : 1;
            return engine;
        }
    }

    internal static void Release(IEngineService engine)
    {
        bool applied = false;
        lock (_lock)
        {
            if (!_busy.TryGetValue(engine, out var n)) return;
            if (n > 1)
            {
                _busy[engine] = n - 1;
                return;
            }

            _busy.Remove(engine);
            if (_pendingKey != null && ReferenceEquals(engine, _currentEngine))
            {
                ReplaceEngineLocked(_pendingKey);
                _pendingKey = null;
                applied = true;
            }
        }

        if (applied)
            AfterSwitch();
    }

    // ── Python launcher (Legacy) ────────────────────────────────────────────

    private static void StartPythonBackend()
    {
        lock (_launcherLock)
        {
            if (_launcher != null) return;

            _launcher = new BackendLauncher();
            _launcher.OutputReceived += line => OutputReceived?.Invoke(line);
            try
            {
                _launcher.Start();
            }
            catch (Exception ex)
            {
                OutputReceived?.Invoke($"[backend-error] Could not start Python backend: {ex.Message}");
            }
        }
    }

    private static void StopPythonBackend()
    {
        lock (_launcherLock)
        {
            if (_launcher == null) return;
            try
            {
                _launcher.Dispose();
            }
            catch { }
            _launcher = null;
        }
    }

    private static IEngineService CreateEngine(string key) => key switch
    {
        ClassicKey => new BackendService(SettingsService.Current.BackendUrl),
        FerriteKey => new FerriteBackend(),
        _ => new NativeEngineService(),
    };

    public static void Shutdown()
    {
        lock (_lock)
        {
            _currentEngine?.Dispose();
            _currentEngine = null;
            _busy.Clear();
            _pendingKey = null;
        }
        StopPythonBackend();
    }
}
