using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Klang_WinUI.Services;

/// <summary>
/// 后台音频分析（声学特征、音质检测、节奏调性）放在独立的子进程里做。
/// 原因：这些分析用 Media Foundation 解码大量文件，偶尔会在原生层出现访问冲突（0xc0000005），
/// 这种错误 .NET 拦不住，以前会让整个播放器闪退。放到子进程后，出错只会结束子进程：
/// 这一首记为分析失败（本次运行不再重试），子进程按需重新启动，播放器不受影响。
///
/// 同一时间只分析一首（所有请求排队），子进程空闲 2 分钟后自动退出，主程序退出时也会随之结束（标准输入关闭）。
/// 子进程起不来时退回到本进程里分析，功能不受影响，只是失去隔离。
/// 通讯：标准输入 / 输出，每行一个 JSON。
/// </summary>
public sealed class AnalysisWorker
{
    public const string WorkerArg = "--klang-analysis-worker";

    private static readonly Lazy<AnalysisWorker> _instance = new(() => new AnalysisWorker());
    public static AnalysisWorker Instance => _instance.Value;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan IdleExit = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);   // 本次运行里让子进程崩溃 / 卡死过的文件
    private Process? _proc;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private bool _unavailable;      // 子进程启动失败：退回到本进程分析
    private int _nextId;
    private Timer? _idleTimer;

    /// <summary>这首在本次运行中已经让分析进程出过错。</summary>
    public bool HasFailed(string path) { lock (_failed) return _failed.Contains(path); }

    // ---------------- 对外的三个分析 ----------------

    public async Task<float[]?> FeaturesAsync(string path, double duration)
    {
        var r = await RunAsync("feat", path, duration, 0, () => AudioFeatureService.Extract(path, duration) is { } f ? FeatToJson(f) : null);
        return r is JsonArray a ? a.Select(x => (float)x!).ToArray() : null;
    }

    public async Task<AuthResult?> QualityAsync(string path, double duration, int bits)
    {
        var r = await RunAsync("quality", path, duration, bits, () => QualityAnalysisService.Analyze(path, duration, bits) is { } q ? QualityToJson(q) : null);
        return r is JsonObject o ? QualityFromJson(o) : null;
    }

    public async Task<RhythmResult?> RhythmAsync(string path, double duration)
    {
        var r = await RunAsync("rhythm", path, duration, 0, () => RhythmAnalysisService.Analyze(path, duration) is { } x ? RhythmToJson(x) : null);
        return r is JsonObject o ? RhythmFromJson(o) : null;
    }

    // ---------------- 主进程：请求与子进程管理 ----------------

    private async Task<JsonNode?> RunAsync(string op, string path, double duration, int bits, Func<JsonNode?> inProcess)
    {
        if (HasFailed(path)) return null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            // 记下正在分析哪首：万一出问题，错误日志里能看到是哪个文件
            CrashReporter.Note($"分析（{op}）：{Path.GetFileName(path)}");

            if (_unavailable || !EnsureStarted())
                return await Task.Run(inProcess).ConfigureAwait(false);

            int id = ++_nextId;
            var req = new JsonObject { ["id"] = id, ["op"] = op, ["path"] = path, ["duration"] = duration, ["bits"] = bits };
            string? line;
            try
            {
                await _stdin!.WriteLineAsync(req.ToJsonString()).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);
                var read = _stdout!.ReadLineAsync();
                var done = await Task.WhenAny(read, Task.Delay(RequestTimeout)).ConfigureAwait(false);
                line = done == read ? await read.ConfigureAwait(false) : null;
                if (done != read) Fail(path, op, "超时（" + (int)RequestTimeout.TotalSeconds + " 秒无响应）");
            }
            catch (IOException) { line = null; }

            if (line == null)
            {
                if (_proc != null) Fail(path, op, ExitDescription());
                return null;
            }

            var resp = JsonNode.Parse(line);
            if (resp?["ok"]?.GetValue<bool>() != true) return null;   // 普通的解码失败（不支持的格式等），子进程正常
            return resp["result"]?.DeepClone();
        }
        finally
        {
            _idleTimer ??= new Timer(_ => StopIdle(), null, Timeout.Infinite, Timeout.Infinite);
            _idleTimer.Change(IdleExit, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    private string ExitDescription()
    {
        try
        {
            if (_proc != null && _proc.WaitForExit(2000))
                return $"分析进程异常退出（代码 0x{_proc.ExitCode:X8}）";
        }
        catch { }
        return "分析进程无响应";
    }

    /// <summary>这首让子进程崩溃 / 卡死：记录下来，本次运行不再分析它，并结束子进程（下次请求时重新启动）。</summary>
    private void Fail(string path, string op, string why)
    {
        lock (_failed) _failed.Add(path);
        App.LogError("Analysis worker", $"{why}：{op} {path}（已跳过这首，播放器不受影响）");
        StopProcess();
    }

    private bool EnsureStarted()
    {
        if (_proc is { HasExited: false }) return true;
        StopProcess();
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) { _unavailable = true; return false; }
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add(WorkerArg);
            _proc = Process.Start(psi);
            if (_proc == null) { _unavailable = true; return false; }
            try { _proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // 不和播放抢 CPU
            _stdin = _proc.StandardInput;
            _stdout = _proc.StandardOutput;
            return true;
        }
        catch (Exception ex)
        {
            _unavailable = true;
            App.LogError("Analysis worker start", ex.Message + "（改为在本进程分析）");
            return false;
        }
    }

    private void StopIdle()
    {
        if (!_gate.Wait(0)) return;   // 正在分析就不停
        try { StopProcess(); } finally { _gate.Release(); }
    }

    private void StopProcess()
    {
        try { _stdin?.Dispose(); } catch { }
        try { if (_proc is { HasExited: false }) { if (!_proc.WaitForExit(1500)) _proc.Kill(); } } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null; _stdin = null; _stdout = null;
    }

    // ---------------- 子进程：读请求、分析、回结果 ----------------

    /// <summary>子进程入口：在多线程套间的线程上逐行处理请求，标准输入关闭时退出。</summary>
    public static int RunWorker()
    {
        int code = 0;
        var t = new Thread(() =>
        {
            try
            {
                var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                string? line;
                while ((line = input.ReadLine()) != null)
                {
                    var resp = new JsonObject();
                    try
                    {
                        var req = JsonNode.Parse(line)!;
                        resp["id"] = req["id"]!.GetValue<int>();
                        string op = req["op"]!.GetValue<string>(), path = req["path"]!.GetValue<string>();
                        double dur = req["duration"]!.GetValue<double>();
                        int bits = req["bits"]?.GetValue<int>() ?? 0;
                        resp["result"] = op switch
                        {
                            "feat" => AudioFeatureService.Extract(path, dur) is { } f ? FeatToJson(f) : null,
                            "quality" => QualityAnalysisService.Analyze(path, dur, bits) is { } q ? QualityToJson(q) : null,
                            "rhythm" => RhythmAnalysisService.Analyze(path, dur) is { } r ? RhythmToJson(r) : null,
                            _ => null
                        };
                        resp["ok"] = true;
                    }
                    catch (Exception ex)
                    {
                        resp["ok"] = false;
                        resp["error"] = ex.Message;
                    }
                    output.WriteLine(resp.ToJsonString());
                }
            }
            catch { code = 1; }
        });
        t.SetApartmentState(ApartmentState.MTA);
        t.IsBackground = false;
        t.Start();
        t.Join();
        return code;
    }

    // ---------------- 结果的 JSON 表示 ----------------

    private static JsonNode FeatToJson(float[] f) => new JsonArray(f.Select(x => (JsonNode?)JsonValue.Create(float.IsFinite(x) ? x : 0f)).ToArray());

    private static JsonNode QualityToJson(AuthResult q) => new JsonObject
    {
        ["verdict"] = q.Verdict, ["cutoff"] = q.CutoffHz, ["bits"] = q.EffectiveBits, ["sr"] = q.SampleRate,
        ["nyq"] = q.NyquistHz,
        ["spec"] = new JsonArray(q.SpectrumDb.Select(x => (JsonNode?)JsonValue.Create(float.IsFinite(x) ? x : -200f)).ToArray())
    };

    private static AuthResult QualityFromJson(JsonObject o) => new(
        (string)o["verdict"]!, (float)o["cutoff"]!, (int)o["bits"]!, (int)o["sr"]!,
        o["spec"]!.AsArray().Select(x => (float)x!).ToArray(), (float)o["nyq"]!);

    private static double D(double x) => double.IsFinite(x) ? x : 0;

    private static JsonNode RhythmToJson(RhythmResult r) => new JsonObject
    {
        ["bpm"] = D(r.Bpm), ["first"] = D(r.FirstBeat), ["last"] = D(r.LastBeat), ["end"] = D(r.MixEnd),
        ["key"] = r.Key, ["camelot"] = r.Camelot, ["conf"] = float.IsFinite(r.Confidence) ? r.Confidence : 0f
    };

    private static RhythmResult RhythmFromJson(JsonObject o) => new(
        (double)o["bpm"]!, (double)o["first"]!, (double)o["last"]!, (double)o["end"]!,
        (string)o["key"]!, (string)o["camelot"]!, (float)o["conf"]!);
}
