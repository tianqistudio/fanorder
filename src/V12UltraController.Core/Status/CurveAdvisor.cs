using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using V12UltraController.Core.Configuration;
using V12UltraController.Core.Control;

namespace V12UltraController.Core.Status;

public sealed record ThermalObservation(DateTimeOffset Time, double? Temperature, byte? ReportedPercent,
    bool Automatic, bool Protected, string CurveId);
public sealed record CurveAdviceRow(double TemperatureCelsius, byte Before, byte After, int Samples, string Reason);
public sealed record CurveAdvice(DateTimeOffset GeneratedAt, int RecordedSamples, int EligibleSamples,
    FanCurve Baseline, FanCurve Suggested, IReadOnlyList<CurveAdviceRow> Rows, string Note, string? StorageWarning = null);

/// <summary>Conservative local heuristics, not a causal cooling model or airflow measurement.</summary>
public static class CurveAdvisor
{
    public static string Identity(FanCurve curve) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(curve.Points))));

    public static ThermalObservation Capture(DateTimeOffset now, ControllerStatus status, FanCurve curve)
    {
        bool Fresh(DateTimeOffset? stamp) => stamp is { } value && now >= value && now - value <= TimeSpan.FromSeconds(10);
        var valid = status.Commissioned && status.Device.Connected && status.Device.ProtocolCompatible &&
            !status.HardwareSafety.Poisoned && status.Thermal.Healthy && Fresh(status.Thermal.LastUpdated) &&
            Fresh(status.LastSuccessfulRead) && status.Fan.PoweredOn == true &&
            status.Thermal.ControlCelsius is >= 0 and <= 120 && status.Fan.ReportedControlPercent is >= 10 and <= 100;
        return new(now, valid ? status.Thermal.ControlCelsius : null,
            valid ? status.Fan.ReportedControlPercent : null, status.Fan.Mode == ControlMode.Auto,
            status.FailSafe.Active, Identity(curve));
    }

    public static CurveAdvice Analyze(IEnumerable<ThermalObservation> history, FanCurve baseline, DateTimeOffset now)
    {
        baseline.Validate();
        var id = Identity(baseline);
        // One observation per minute prevents duplicate samples from overstating evidence.
        var recorded = history.Where(s => s.Time <= now && s.Time >= now.AddDays(-7))
            .OrderBy(s => s.Time).GroupBy(s => s.Time.ToUnixTimeSeconds() / 60).Select(g => g.First()).ToArray();
        bool Eligible(ThermalObservation s) => s.Automatic && !s.Protected && s.CurveId == id &&
            s.Temperature is >= 0 and <= 120 && s.ReportedPercent is >= 10 and <= 100;
        var eligible = recorded.Where(Eligible).ToArray();
        var pairs = recorded.Zip(recorded.Skip(1)).Where(p => Eligible(p.First) && Eligible(p.Second) &&
            (p.Second.Time - p.First.Time).TotalSeconds is >= 45 and <= 90).ToArray();
        var rows = new List<CurveAdviceRow>();
        foreach (var point in baseline.Points)
        {
            var local = eligible.Where(s => Math.Abs(s.Temperature!.Value - point.TemperatureCelsius) <= 5).ToArray();
            var localPairs = pairs.Where(p => Math.Abs(p.First.Temperature!.Value - point.TemperatureCelsius) <= 5).ToArray();
            var slopes = localPairs.Select(p => (p.Second.Temperature!.Value - p.First.Temperature!.Value) /
                (p.Second.Time - p.First.Time).TotalMinutes).ToArray();
            var next = (int)point.FanPercent;
            var reason = "保持：该温区同曲线自动模式样本不足（至少30分钟及20对连续样本）。";
            if (point.TemperatureCelsius >= 90)
            {
                next = 100;
                reason = "安全上限：90°C及以上建议100%；运行时紧急保护始终独立有效。";
            }
            else if (local.Length >= 30 && slopes.Length >= 20)
            {
                var rising = slopes.Count(s => s >= 0.5) / (double)slopes.Length;
                var meanSpeed = local.Average(s => s.ReportedPercent!.Value);
                var meanTemperature = local.Average(s => s.Temperature!.Value);
                if (rising >= 0.25 || (point.TemperatureCelsius >= 80 && meanTemperature >= 85))
                {
                    next = Math.Min(100, next + 5);
                    reason = $"建议+5：{local.Length}个分钟样本，快速升温占{rising:P0}；保守增加散热余量，非因果结论。";
                }
                else if (point.TemperatureCelsius < 70 && local.Length >= 120 && slopes.Length >= 100 &&
                    slopes.All(s => Math.Abs(s) <= 0.2) && meanSpeed >= point.FanPercent + 5 &&
                    !recorded.Any(s => s.Protected || s.Temperature is >= 85))
                {
                    next = Math.Max(10, next - 3);
                    reason = $"可试降3：{local.Length}个分钟样本稳定，实际控制均值{meanSpeed:F0}%；需继续观察，不能保证降速后的温度。";
                }
                else reason = $"保持：{local.Length}个分钟样本不足以支持明确调整。";
            }
            rows.Add(new(point.TemperatureCelsius, point.FanPercent, (byte)next, local.Length, reason));
        }
        // Never introduce a falling curve; constrain local changes to existing neighbors.
        for (var i = 0; i < rows.Count; i++)
        {
            var minimum = i == 0 ? 10 : Math.Max(rows[i - 1].After, rows[i - 1].Before);
            var maximum = i == rows.Count - 1 ? 100 : rows[i + 1].Before;
            if (rows[i].TemperatureCelsius >= 90) maximum = 100;
            var after = (byte)Math.Clamp(rows[i].After, minimum, Math.Max(minimum, maximum));
            if (after != rows[i].After) rows[i] = rows[i] with { After = after, Reason = rows[i].Reason + " 已按单调曲线约束限制幅度。" };
        }
        var suggested = new FanCurve(rows.Select(r => new FanCurvePoint(r.TemperatureCelsius, r.After)).ToArray());
        suggested.Validate();
        var manualHot = recorded.Count(s => !s.Automatic && s.Temperature is >= 85 and <= 120);
        return new(now, recorded.Length, eligible.Length, baseline, suggested, rows,
            $"最近7天本地历史；手动模式≥85°C记录{manualHot}分钟（风险提示，不用于量化降速建议）。百分比不是实测风量/RPM。排除手动、保护、缺失、旧曲线样本及断点；未记录功耗/环境温度，建议仅供试调，不证明散热因果。应用将切换自动模式，紧急保护不变。");
    }
}
