using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CellWar.Ai;
using CellWar.Core.Worlds;

namespace CellWar.Core.Tests.Ai;

/// <summary>
/// AI 放大招的**放了几次、空放几次**（Kevin 10-09：只按这个验收，不跑胜率、不报胜负）。不进日常套件：
/// <c>CW_AI_ULT_COUNT=1 dotnet test -c Release --filter UltimateCastCount</c>，报告写到测试输出目录 ult_count.txt。
///
/// · 普通档：整局全 AI 自对弈，6 人 <c>CW_AI_ULT_N6</c>（默认 600）+ 4 人 <c>CW_AI_ULT_N4</c>（默认 300）局，种子 1..N；
/// · 意图 / 搜索档：整局走到 X 级的太少、太慢，从普通档里**门槛开过**的局取样，全席换成这一档接着打到终局
///   （意图 <c>CW_AI_ULT_INTENT</c>、搜索 <c>CW_AI_ULT_SEARCH</c> 默认都是全部取样，搜索档带产品预算 2500 ms）：
///   意图档从第一次开门那一回合往前 <c>CW_AI_ULT_LEAD</c>（默认 1）回合的开头打起，门槛多半还会自己开；
///   搜索档换了走法门槛常常不开了（试跑：第 10 回合起取样 3 局一次没开、往前 1 回合 5 局只开了 1 局），所以直接从普通档第一次开门的那一问打起。
/// 「空放」：Excalibur 一格没翻、一点没打；中和抗体一个都没压住；连续吞噬那一回合一跳没连；
/// 免疫猎杀没有真正的零效果（追踪趋化源总会挂上），报两样 —— 没加上新标记的、3 格内一个能出手的队友都没有的（两样都占才记空放）。
/// 断言只有「零空放」这类放法本身保证的（报告先写出来再断言）；放了几次只报不断言（局数改小了自然会少）。
/// 2026-10-09 跑过一次（默认局数，Release，墙钟约 4 分钟），数字记在开发日志同日条目。
/// <c>CW_AI_ULT_DUMP=目录</c> 时，把普通档门槛开着的局面（每种大招放 / 不放各取两个）导成 cwxworld/3，回归夹具从这里挑。
/// </summary>
public class UltimateCastCount
{
    private static readonly BasicRulesEngine Engine = new();
    private static readonly string[] Ultimates = ["Excalibur", "中和抗体", "免疫猎杀", "连续吞噬"];

    private sealed class Tally
    {
        public int Games, GamesWithGate, GateAsks;
        public readonly ConcurrentBag<long> GateMicros = [];
        public readonly ConcurrentDictionary<string, int> Casts = new(), Empty = new(), GateBy = new();
        public readonly ConcurrentBag<(string What, int Round)> CastRounds = [];
        public int HuntNoMark, HuntNoAlly, HuntNoAllyEarly, ExcaliburZeroScore;
        public readonly ConcurrentBag<int> ChainHops = [];

        public void Add(ConcurrentDictionary<string, int> d, string k) => d.AddOrUpdate(k, 1, (_, v) => v + 1);
    }

    /// <summary>一局门槛开过的普通档对局的取样：往前几回合的开头（意图档用）与第一次开门的那一问（搜索档用）。</summary>
    private sealed record Harvest(int Players, ulong Seed, WorldState Lead, WorldState AtGate);

    [Fact]
    public void 三档_每个大招放了几次_空放几次()
    {
        if (Environment.GetEnvironmentVariable("CW_AI_ULT_COUNT") is null) return;
        int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
        var dumpDir = Environment.GetEnvironmentVariable("CW_AI_ULT_DUMP");
        var lead = Env("CW_AI_ULT_LEAD", 1);
        var lines = new List<string> { $"# AI 放大招计数（{BuildKind()}；只数放了几次、空放几次，不报胜负）" };

        // ---- 普通档整局 ----
        var normal = new Tally();
        var tallies = new List<(string Tier, Tally Tally)> { ("普通档", normal) };
        var harvests = new ConcurrentBag<Harvest>();
        var dumps = new ConcurrentDictionary<string, int>();
        var clock = Stopwatch.StartNew();
        var games = new List<(int Players, ulong Seed)>();
        for (ulong seed = 1; seed <= (ulong)Env("CW_AI_ULT_N6", 600); seed++) games.Add((6, seed));
        for (ulong seed = 1; seed <= (ulong)Env("CW_AI_ULT_N4", 300); seed++) games.Add((4, seed));
        Parallel.ForEach(games, g =>
        {
            var policy = AiPolicies.Create(AiConfig.For(AiTier.Normal));
            var roundStart = new Dictionary<int, WorldState>();
            var (firstGate, atGate) = Play(MatchSetup.Create(g.Players, g.Seed), g.Seed, policy, normal, roundStart, dumpDir, dumps, $"{g.Players}p_{g.Seed}");
            if (atGate != null && roundStart.TryGetValue(Math.Max(1, firstGate - lead), out var st))
                harvests.Add(new Harvest(g.Players, g.Seed, st, atGate));
        });
        lines.AddRange(Report("普通档", normal, $"整局 6 人 {games.Count(x => x.Players == 6)} + 4 人 {games.Count(x => x.Players == 4)} 局", clock.Elapsed));

        var sample = harvests.OrderBy(h => h.Players).ThenBy(h => h.Seed).ToList();
        lines.Add($"# 取样：门槛开过的局 {sample.Count} 个（6 人 {sample.Count(h => h.Players == 6)} / 4 人 {sample.Count(h => h.Players == 4)}）；意图档从第一次开门往前 {lead} 回合的开头打起，搜索档从第一次开门的那一问打起");

        // ---- 意图 / 搜索档：从取样局面接着打 ----
        foreach (var (tier, take) in new[] { (AiTier.Intent, Env("CW_AI_ULT_INTENT", sample.Count)), (AiTier.Search, Env("CW_AI_ULT_SEARCH", sample.Count)) })
        {
            var tally = new Tally();
            tallies.Add((tier.ToString(), tally));
            var picked = Spread(sample, take);
            clock.Restart();
            Parallel.ForEach(picked, new ParallelOptions { MaxDegreeOfParallelism = tier == AiTier.Search ? 12 : -1 }, h =>
            {
                var policy = AiPolicies.Create(new AiConfig { Tier = tier, BudgetMs = tier == AiTier.Search ? 2500 : 0 });
                Play(tier == AiTier.Intent ? h.Lead : h.AtGate, h.Seed * 7919 + (ulong)h.Players, policy, tally, null, null, null, "");
            });
            lines.AddRange(Report(tier == AiTier.Intent ? "意图档" : "搜索档", tally,
                $"取样局面 {picked.Count} 个（6 人 {picked.Count(h => h.Players == 6)} / 4 人 {picked.Count(h => h.Players == 4)}）接着打到终局", clock.Elapsed));
        }

        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "ult_count.txt"), lines);
        // 零空放是放法的硬保证（报告先写出来再断言）：Excalibur 不朝 0 分方向放、中和抗体不零目标放、连续吞噬不放一跳都不连的、
        // 第 14 回合之前不对 3 格内没队友的目标放猎杀
        foreach (var (tier, t) in tallies)
        {
            Assert.True(t.Empty.IsEmpty, $"{tier} 有空放：{string.Join("、", t.Empty.Select(kv => $"{kv.Key} {kv.Value}"))}");
            Assert.Equal(0, t.ExcaliburZeroScore);
            Assert.Equal(0, t.HuntNoAllyEarly);
        }
    }

    /// <summary>均匀取 n 个（保持 6 人 / 4 人的比例大致不变）。</summary>
    private static List<Harvest> Spread(List<Harvest> all, int n)
    {
        if (n >= all.Count) return all;
        return Enumerable.Range(0, n).Select(i => all[(int)((long)i * all.Count / n)]).ToList();
    }

    /// <summary>一局打到终局：真局骰子走 xoshiro（与产品同），每问的推演种子由局种子 + 步数派生（同 sidecar）。
    /// 返回第一次门槛开着的回合（0 = 没开过）与那一问的局面。</summary>
    private static (int FirstGate, WorldState? AtGate) Play(WorldState s, ulong seed, IPolicy policy, Tally tally, Dictionary<int, WorldState>? roundStart,
        string? dumpDir, ConcurrentDictionary<string, int>? dumps, string tag)
    {
        var rng = new Xoshiro256StarStar(seed);
        var step = 0L;
        var firstGate = 0;
        WorldState? atGate = null;
        var chains = new List<(EntityId Cell, int Round)>();
        var hops = new Dictionary<(EntityId, int), int>();
        while (s.Turn.Phase != Phase.Finished && step < 40000)
        {
            var (seat, options) = RolloutCursorProbe.NextAsked(s);
            if (options.Count == 0) { s = Engine.AdvancePhase(s, rng).NewState; continue; }
            if (roundStart != null && !roundStart.ContainsKey(s.Turn.WorldRound)) roundStart[s.Turn.WorldRound] = s;
            var gate = GateOpen(s, seat, options);
            var watch = Stopwatch.StartNew();
            var key = policy.Choose(s, seat, options, new SplitMix64Rng(SplitMix64Rng.DecisionSeed(seed, step++)));
            var micros = watch.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
            var d = options.First(o => SemanticKey.Of(s, o) == key);
            if (gate != null)
            {
                Interlocked.Increment(ref tally.GateAsks);
                tally.Add(tally.GateBy, gate);
                tally.GateMicros.Add(micros);
                if (firstGate == 0) (firstGate, atGate) = (s.Turn.WorldRound, s);
                if (dumpDir != null) Dump(dumpDir, dumps!, s, gate, key, tag);
            }
            var before = s;
            s = Engine.ExecuteDecision(s, d, rng).NewState;
            if (d is TypeSkillDecision t && Ultimates.Contains(t.Skill))
            {
                Record(tally, before, s, t);
                if (t.Skill == "连续吞噬") chains.Add((t.CellId, before.Turn.WorldRound));
            }
            if (d is ChainMoveDecision hop) hops[(hop.CellId, before.Turn.WorldRound)] = hops.GetValueOrDefault((hop.CellId, before.Turn.WorldRound)) + 1;
        }
        foreach (var c in chains)
        {
            var n = hops.GetValueOrDefault(c);
            tally.ChainHops.Add(n);
            if (n == 0) tally.Add(tally.Empty, "连续吞噬");
        }
        Interlocked.Increment(ref tally.Games);
        if (firstGate > 0) Interlocked.Increment(ref tally.GamesWithGate);
        return (firstGate, atGate);
    }

    /// <summary>这一问门槛开着吗（免疫方顶层行动问答里给出了效应应答，或【连续吞噬】刚放还没接上）：返回那个大招的名字。</summary>
    private static string? GateOpen(WorldState s, int seat, IReadOnlyList<IDecision> options)
    {
        if (seat < 0 || s.Players[seat].Faction != Faction.Immune) return null;
        if (options.OfType<TypeSkillDecision>().FirstOrDefault(o => Ultimates.Contains(o.Skill)) is { } eff) return eff.Skill;
        var me = s.Cells.Values.FirstOrDefault(c => c.OwnerSeat == seat && c.IsAlive);
        return me is { ChainLeft: CellRules.ChainPhagoMax } && AskView.KindOf(s, options) == "action" ? "连续吞噬（接连锁）" : null;
    }

    private static void Record(Tally tally, WorldState before, WorldState after, TypeSkillDecision t)
    {
        tally.Add(tally.Casts, t.Skill);
        tally.CastRounds.Add((t.Skill, before.Turn.WorldRound));
        var caster = before.Cells[t.CellId];
        switch (t.Skill)
        {
            case "Excalibur":
            {
                var flipped = before.Board.Tissues.Keys.Count(p => before.Board.Tissues[p].State == TissueState.Cancer && after.Board.Tissues[p].State == TissueState.Healthy);
                var dealt = before.Cells.Values.Where(c => c.Faction == Faction.Cancer && c.IsAlive).Sum(c => Math.Max(0, c.Energy - after.Cells[c.Id].Energy));
                if (flipped == 0 && dealt == 0) tally.Add(tally.Empty, t.Skill);
                if (UltimatePolicy.ExcaliburScore(before, caster.Position, t.Target!.Value) == 0) Interlocked.Increment(ref tally.ExcaliburZeroScore);
                break;
            }
            case "中和抗体":
                if (!before.Cells.Values.Any(c => c.Faction == Faction.Cancer && after.Cells[c.Id].NeutralUntil != c.NeutralUntil)) tally.Add(tally.Empty, t.Skill);
                break;
            case "免疫猎杀":
            {
                var target = before.Cells[t.TargetCell!.Value];
                var newMark = target.MarkRound != before.Turn.WorldRound && after.Cells[target.Id].MarkRound == before.Turn.WorldRound;
                var allies = before.Cells.Values.Count(c => c.IsAlive && c.Type is CellType.TCell or CellType.BCell or CellType.Macrophage
                    && c.Position.DistanceTo(target.Position) <= 3);
                if (!newMark) Interlocked.Increment(ref tally.HuntNoMark);
                if (allies == 0)
                {
                    Interlocked.Increment(ref tally.HuntNoAlly);
                    if (before.Turn.WorldRound < UltimatePolicy.UrgentRound) Interlocked.Increment(ref tally.HuntNoAllyEarly);
                }
                if (!newMark && allies == 0) tally.Add(tally.Empty, t.Skill);
                break;
            }
        }
    }

    private static readonly JsonSerializerOptions DumpJson = new(WorldJson.Options) { WriteIndented = false };

    /// <summary>每种大招「放」「不放」各导两个局面（文件名带上普通档的答案），回归夹具从这里挑。</summary>
    private static void Dump(string dir, ConcurrentDictionary<string, int> dumps, WorldState s, string gate, string key, string tag)
    {
        var bucket = $"{gate}_{(UltimatePolicyTests.IsCast(key) || key.Contains("act=move", StringComparison.Ordinal) && gate.EndsWith("（接连锁）", StringComparison.Ordinal) ? "cast" : "hold")}";
        if (dumps.AddOrUpdate(bucket, 1, (_, v) => v + 1) > 2) return;
        Directory.CreateDirectory(dir);
        var spec = WorldLoader.Minify(WorldLoader.Dump(s));
        var name = $"{bucket}_{tag}_r{s.Turn.WorldRound}";
        File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(spec, DumpJson));
        File.WriteAllText(Path.Combine(dir, name + ".key"), key);
    }

    private static IEnumerable<string> Report(string tier, Tally t, string what, TimeSpan wall)
    {
        yield return "";
        yield return $"## {tier}：{what}（墙钟 {wall.TotalSeconds:F0} s）";
        yield return $"  打完 {t.Games} 局，其中门槛开过的 {t.GamesWithGate} 局；门槛开着的问答 {t.GateAsks} 问（{string.Join("、", t.GateBy.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"))}）";
        foreach (var u in Ultimates)
        {
            var rounds = t.CastRounds.Where(x => x.What == u).GroupBy(x => x.Round).OrderBy(x => x.Key).Select(x => $"第{x.Key}回合×{x.Count()}");
            yield return $"  【{u}】放了 {t.Casts.GetValueOrDefault(u)} 次，空放 {t.Empty.GetValueOrDefault(u)} 次（{string.Join(" ", rounds)}）";
        }
        yield return $"  Excalibur 放的方向战果分为 0 的：{t.ExcaliburZeroScore} 次";
        yield return $"  免疫猎杀：没加上新标记 {t.HuntNoMark} 次；3 格内没有能出手的队友 {t.HuntNoAlly} 次（其中第 14 回合之前 {t.HuntNoAllyEarly} 次）";
        var hops = t.ChainHops.OrderBy(x => x).ToList();
        if (hops.Count > 0) yield return $"  连续吞噬每次连跳：{string.Join(" ", hops.GroupBy(x => x).Select(g => $"{g.Key}跳×{g.Count()}"))}";
        var us = t.GateMicros.OrderBy(x => x).ToList();
        if (us.Count > 0)
            yield return $"  门槛开着的那一问用时：p50 {us[us.Count / 2] / 1000.0:F2} ms  p95 {us[Math.Min(us.Count - 1, (int)(us.Count * 0.95))] / 1000.0:F2} ms  max {us[^1] / 1000.0:F2} ms";
    }

    private static string BuildKind()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }
}
