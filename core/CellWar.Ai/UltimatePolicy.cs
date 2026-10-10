using CellWar.Core;

namespace CellWar.Ai;

/// <summary>
/// 免疫等级 X 的四个大招（【效应应答】）什么时候放、打哪里 —— **Kevin 2026-10-09 拍板的放法**（docs/内核替换_重启计划.md §八「要定的」第 7 条）。
/// 普通 / 意图 / 搜索三档共用这一份（<see cref="NormalPolicy.UltimateStep"/>），放不放、打哪里完全一样。
///
/// 这是 C# AI 与 GD 参照（heuristic_bridge.gd v11，一个大招都不放）的**第三处有意差异**（<see cref="NormalPolicy"/> 头注 ③）：
/// 对拍语料里一问效应应答都没有（录到的局面到不了 X 级），所以这里的行为由 game/tests/ai_ult/ 的夹具钉（UltimatePolicyTests），不由对拍钉。
///
/// 数字都是估的，验收只数「放了几次、有没有空放」，不跑胜率（Kevin）：
/// · 【Excalibur】6 个方向各算「战果分」（十分位）：射线上每格普通癌组织 10、射线两旁每格普通癌组织 6（60% 波及的期望）、
///   射线上每个癌细胞 20（挨 2.0）。最好的方向 ≥ 40 才放。朝盘外 / 整条线什么都没有 = 0 分，永远不放
///   （两个内核都把盘外方向列成选项，放了白扣 20 记忆）。不算【标记】：Excalibur 的伤害记作癌方技能来源，标记不翻倍。
/// · 【中和抗体】贴着健康组织、此刻没被中和的癌细胞 1 分 / 个，身上每张永久卡再 +1（它关掉的正是种类技能与永久卡）；≥ 2 才放。
/// · 【免疫猎杀】每个癌细胞：3 格内能出手的免疫队友（T / B / 巨噬；树突自己不能攻击）1 分 / 个，这次真能给它加上**新**标记再 +1
///   （本回合还没标过、也不在任何树突的 2 格光环里）；最高 ≥ 2 才放。同分打能量最低的（一记翻倍的攻击最可能打死它），再同分取规范序第一个。
/// · 【连续吞噬】这一步就能花钱净化一格时才看（付得起、走进去活得过回合末的压迫 —— 与普通档步骤 7 同一把尺）；
///   那一格所在的那片无人普通癌组织还能免费连跳几格（最多 5）≥ 2 才放。放完下一问先去净化连跳最多的那一格（<see cref="ChainStart"/>）。
///   回合末会被压迫压死的格不算能跳（连跳时也不跳进去，<c>PickFreeMove</c>）—— 否则会放出一条一跳都不肯跳的连锁。
///   【骨样硬化】标记格进去不净化（只蹲守），既不当起点也不算能跳（10-09 复核补）。
/// · 第 14、15 回合（15 回合后强制结算）四个门槛都减半。
///
/// 只读世界、不掷骰：Excalibur 的射线用内核的公开纯查询现走（<see cref="ExcaliburArea"/>），不碰 SkillRules 里掷骰的那段；
/// 盘外的格一律不经 <c>AiView.Tile</c>（它对盘外抛异常）。漂移测试拿真引擎执行结果钉住这份读法。
/// </summary>
internal static class UltimatePolicy
{
    /// <summary>从第几回合起四个门槛减半（15 回合后强制结算，OutcomeRules）。</summary>
    internal const int UrgentRound = 14;
    /// <summary>【Excalibur】最好方向的战果分门槛（十分位：40 = 4 格普通癌组织）。</summary>
    internal const int ExcaliburGood = 40;
    /// <summary>【中和抗体】能压住的分数门槛。</summary>
    internal const int NeutralizeGood = 2;
    /// <summary>【免疫猎杀】最好目标的分数门槛。</summary>
    internal const int HuntGood = 2;
    /// <summary>【连续吞噬】能免费连跳的格数门槛。</summary>
    internal const int ChainGood = 2;

    /// <summary>战果分（十分位）：射线上一格普通癌组织 / 射线上一个癌细胞（挨 2.0）。侧向一格 = 波及概率 60% → 6，直接读规则常量。</summary>
    private const int RayTile = 10;
    private const int RayCell = 20;
    private static int SplashTile => SkillRules.ExcaliburSplashPercent / 10;

    /// <summary>【免疫猎杀】数队友的半径。</summary>
    private const int HuntAllyRange = 3;

    private static int Need(AiView g, int good) => g.Round >= UrgentRound ? good / 2 : good;

    /// <summary>
    /// 门槛开着（效应应答给出来了）时放不放：放就返回那一条的下标，不放 -1。
    /// <paramref name="purges"/> = 这一步能花钱净化的迁移（普通档步骤 7 的口径；只有巨噬用得上）。
    /// </summary>
    internal static int Cast(AiView g, Cell me, IReadOnlyList<AiOption> options, IReadOnlyList<int> purges, bool lifecare)
    {
        var i = NormalPolicy.Find(options, "effector");
        if (i < 0) return -1;
        var worth = me.Type switch
        {
            CellType.TCell => BestExcalibur(g, me, options[i].Children).Score >= Need(g, ExcaliburGood),
            CellType.BCell => NeutralizePoints(g) >= Need(g, NeutralizeGood),
            CellType.Dendritic => BestHunt(g, options[i].Children).Score >= Need(g, HuntGood),
            CellType.Macrophage => ChainStart(g, me, options, purges, lifecare).Hops >= Need(g, ChainGood),
            _ => false,
        };
        return worth ? i : -1;
    }

    /// <summary>效应应答的第二问（落到哪个子项）：Excalibur 挑方向、免疫猎杀挑目标。</summary>
    internal static AiOption PickTarget(AiView g, AiOption group)
    {
        var kids = group.Children;
        var best = kids[0].Dir != null ? BestExcalibur(g, CasterOf(g, kids[0]), kids).Index
            : kids[0].Cid != null ? BestHunt(g, kids).Index
            : 0;
        return kids[Math.Max(best, 0)];
    }

    private static Cell CasterOf(AiView g, AiOption child) => g.S.Cells[((TypeSkillDecision)child.Decision!).CellId];

    // ============ Excalibur ============

    /// <summary>战果分最高的方向（严格大于才换 ⇒ 同分取规范序第一个；全是 0 分时 Index = -1）。</summary>
    private static (int Index, int Score) BestExcalibur(AiView g, Cell me, IReadOnlyList<AiOption> directions)
    {
        var best = (Index: -1, Score: 0);
        for (var i = 0; i < directions.Count; i++)
        {
            var score = ExcaliburScore(g.S, me.Position, directions[i].To!.Value);
            if (score > best.Score) best = (i, score);
        }
        return best;
    }

    /// <summary>朝 <paramref name="aim"/>（T 的一个邻格，可能在盘外）放的战果分，十分位。</summary>
    internal static int ExcaliburScore(WorldState s, HexPosition from, HexPosition aim)
    {
        var (ray, splash) = ExcaliburArea(s, from, aim);
        var score = SplashTile * splash.Count;
        foreach (var p in ray)
        {
            if (s.Board.Tissues[p].State == TissueState.Cancer) score += RayTile;
            if (s.GetCellAt(p) is { IsAlive: true, Faction: Faction.Cancer }) score += RayCell;
        }
        return score;
    }

    /// <summary>
    /// 射线（T 脚下之后一直到盘边，不含脚下）与侧向波及的候选格（射线相邻、不在射线上的普通癌组织，DIRS 序、各算一次）——
    /// SkillRules.Execute 里 Excalibur 那段的同形状读法，只是不掷骰。朝盘外的方向射线为空、候选也为空。
    /// </summary>
    internal static (List<HexPosition> Ray, List<HexPosition> Splash) ExcaliburArea(WorldState s, HexPosition from, HexPosition aim)
    {
        var ray = new List<HexPosition>();
        var splash = new List<HexPosition>();
        if (RulePolicies.RayDirection(from, aim) is not { } dir) return (ray, splash);
        var cursor = from;
        while (true)
        {
            cursor = new HexPosition(cursor.Q + dir.Q, cursor.R + dir.R, cursor.S + dir.S);
            if (!s.Board.Tissues.ContainsKey(cursor)) break;
            ray.Add(cursor);
        }
        var seen = new HashSet<HexPosition>(ray);
        foreach (var pos in ray)
            foreach (var n in RulePolicies.GdNeighbors(s, pos))
                if (s.Board.Tissues[n].State == TissueState.Cancer && seen.Add(n)) splash.Add(n);
        return (ray, splash);
    }

    // ============ 中和抗体 ============

    /// <summary>能压住几样：贴着健康组织（与结算同一个判据）、此刻没被中和的癌细胞 1 分，身上每张永久卡 +1。</summary>
    internal static int NeutralizePoints(AiView g)
    {
        var points = 0;
        foreach (var c in g.Living(Faction.Cancer))
            if (RulePolicies.AdjacentHealthy(g.S, c.Position) && !RulePolicies.Neutralized(g.S, c))
                points += 1 + c.Equipped.Count;
        return points;
    }

    // ============ 免疫猎杀 ============

    /// <summary>分最高的目标（同分打能量最低的，再同分取规范序第一个；全是 0 分时 Index = -1）。</summary>
    private static (int Index, int Score) BestHunt(AiView g, IReadOnlyList<AiOption> targets)
    {
        var best = (Index: -1, Score: 0, Energy: int.MaxValue);
        for (var i = 0; i < targets.Count; i++)
        {
            if (g.CellOf(targets[i].Cid!.Value) is not { IsAlive: true } t) continue;
            var score = HuntScore(g, t);
            if (score > best.Score || (score == best.Score && score > 0 && t.Energy < best.Energy)) best = (i, score, t.Energy);
        }
        return (best.Index, best.Score);
    }

    /// <summary>3 格内能出手的免疫队友数（T / B / 巨噬）+ 这次能加上新标记的 1 分。</summary>
    internal static int HuntScore(AiView g, Cell target)
    {
        var score = 0;
        foreach (var c in g.Living(Faction.Immune))
            if (c.Type is CellType.TCell or CellType.BCell or CellType.Macrophage && AiView.Dist(c.Position, target.Position) <= HuntAllyRange)
                score++;
        var inAura = g.Living(Faction.Immune).Any(d => d.Type == CellType.Dendritic && AiView.Dist(d.Position, target.Position) <= NormalPolicy.MarkRange);
        if (target.MarkRound != g.Round && !inAura) score++;
        return score;
    }

    // ============ 连续吞噬 ============

    /// <summary>
    /// 连锁从哪一格起：<paramref name="purges"/> 里能连跳最多的那一格（同数取癌性邻格多的 —— 普通档步骤 7 的偏好，再同取规范序第一个）。
    /// 【骨样硬化】标记格不算起点（<see cref="Ossified"/>）。没有能花钱净化的格时 Index = -1、Hops = 0。
    /// </summary>
    internal static (int Index, int Hops) ChainStart(AiView g, Cell me, IReadOnlyList<AiOption> options, IReadOnlyList<int> purges, bool lifecare)
    {
        var best = (Index: -1, Hops: -1, Near: -1);
        foreach (var i in purges)
        {
            var to = options[i].To!.Value;
            if (Ossified(g, to)) continue;
            var hops = ChainHops(g, to, me.Energy - options[i].CostOr0, lifecare);
            var near = g.CancerousNeighbors(to);
            if (hops > best.Hops || (hops == best.Hops && near > best.Near)) best = (i, hops, near);
        }
        return (best.Index, Math.Max(best.Hops, 0));
    }

    /// <summary>
    /// 从 <paramref name="start"/>（花钱净化的那一格）起，同一片无人普通癌组织（连跳的落点口径，CellRules.ChainTargets）还能免费连跳几格，
    /// 最多 <see cref="CellRules.ChainPhagoMax"/>。惜命开着时，回合末的压迫会把自己（付完这一步之后的能量）压死的格不算；【骨样硬化】标记格不算。
    /// </summary>
    internal static int ChainHops(AiView g, HexPosition start, int energyAfter, bool lifecare)
    {
        var seen = new HashSet<HexPosition> { start };
        var queue = new Queue<HexPosition>();
        queue.Enqueue(start);
        while (queue.TryDequeue(out var cur))
            foreach (var n in g.Neighbors(cur))
            {
                if (seen.Count > CellRules.ChainPhagoMax) return CellRules.ChainPhagoMax;
                if (seen.Contains(n) || g.Tile(n).State != TissueState.Cancer || g.CellsAtCount(n) > 0 || Ossified(g, n)) continue;
                if (lifecare && energyAfter <= RulePolicies.PressureAt(g.S, n)) continue;
                seen.Add(n);
                queue.Enqueue(n);
            }
        return Math.Min(seen.Count - 1, CellRules.ChainPhagoMax);
    }

    /// <summary>
    /// 骨肉瘤【骨样硬化】标记过的格：免疫走进去只登记蹲守、不净化，连锁也挂不上（CellRules.Arrive）——
    /// 它既当不了连锁的起点，也不算能跳（跳进去连锁就断了），不然会放出一条一格都净化不了的空连锁。
    /// 普通档步骤 7 照旧会走进这种格（GD v11 同样），这里不动它。
    /// </summary>
    private static bool Ossified(AiView g, HexPosition p) => g.Tile(p).OssifyAtRound > 0;
}
