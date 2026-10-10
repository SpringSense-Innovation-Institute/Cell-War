using CellWar.Ai;
using CellWar.Core.Tests.L0;
using CellWar.Core.Worlds;

namespace CellWar.Core.Tests.Ai;

/// <summary>
/// AI 放大招（免疫等级 X 的四个效应应答，Kevin 2026-10-09 拍板的放法 = <see cref="UltimatePolicy"/>，NormalPolicy 头注第 ③ 条）。
///
/// 对拍语料一问效应应答都没有（<see cref="对拍语料里一问效应应答都没有"/>），这一支只能靠这里的夹具钉：
/// 盘面 = game/tests/ai_ult/*.json（cwxworld/3，WorldLoader 装），**三档必须答同一个键**（放不放、打哪里完全一样）。
/// 门槛的边界各钉一块板（正好够 / 差一点 / 第 14 回合减半），变异检验靠它们变红。
/// 内核那边若加了「中和抗体零目标不给按钮」的修正，这里照样绿：零目标的板只断言「不放」，不断言按钮在不在。
/// </summary>
public class UltimatePolicyTests
{
    private static readonly BasicRulesEngine Engine = new();
    private static readonly AiTier[] Tiers = [AiTier.Normal, AiTier.Intent, AiTier.Search];

    internal static string BoardDir() => Path.Combine(L0RunnerTests.GameTestsDir(), "ai_ult");

    internal static WorldSpec Spec(string name) => WorldJson.Parse(File.ReadAllText(Path.Combine(BoardDir(), name + ".json")));

    private static WorldState Board(string name, Func<WorldSpec, WorldSpec>? edit = null)
    {
        var spec = Spec(name);
        return WorldLoader.Load(edit is null ? spec : edit(spec));
    }

    private static Func<WorldSpec, WorldSpec> AtRound(int round) => w => w with { Round = round };

    /// <summary>放大招的键：效应应答（普通 / 组键）。</summary>
    internal static bool IsCast(string key)
        => key == "k=action|act=effector" || key.StartsWith("k=action+effector_target|", StringComparison.Ordinal);

    private static string Answer(IPolicy policy, WorldState s)
    {
        var (seat, options) = RolloutCursorProbe.NextAsked(s);
        return policy.Choose(s, seat, options, new SplitMix64Rng(7));
    }

    private static string Answer(AiTier tier, WorldState s) => Answer(AiPolicies.Create(AiConfig.For(tier)), s);

    private static void AssertAllTiers(WorldState s, string expected, string board)
    {
        foreach (var tier in Tiers)
            Assert.True(Answer(tier, s) == expected, $"{board}：{tier} 档答 {Answer(tier, s)}，应为 {expected}");
    }

    private static void AssertNoTierCasts(WorldState s, string board)
    {
        foreach (var tier in Tiers)
        {
            var key = Answer(tier, s);
            Assert.False(IsCast(key), $"{board}：{tier} 档放了大招（{key}），按放法不该放");
        }
    }

    private static WorldState Execute(WorldState s, string key, IDeterministicRng rng)
    {
        var (_, options) = RolloutCursorProbe.NextAsked(s);
        var result = Engine.ExecuteDecision(s, options.First(o => SemanticKey.Of(s, o) == key), rng);
        Assert.True(result.Success, result.ErrorMessage);
        return result.NewState;
    }

    // ============ Excalibur ============

    [Fact]
    public void Excalibur_战果分正好40_三档都朝东放()
        // 东边一条 4 格癌组织 = 40；规范序第一个子项是西边（to=-1,1），落回 Children[0] 就红
        => AssertAllTiers(Board("excalibur_line"), "k=action+effector_target|act=effector|to=1,1|dir=0", "excalibur_line");

    [Fact]
    public void Excalibur_贴边的T_盘外方向记0分_朝唯一有东西的西边放()
    {
        var s = Board("excalibur_edge");
        AssertAllTiers(s, "k=action+effector_target|act=effector|to=5,-2|dir=3", "excalibur_edge");
        var t = WorldLoader.CellOfSeat(s, 0);
        foreach (var offBoard in new[] { new HexPosition(7, -2, -5), new HexPosition(7, -3, -4) })
        {
            Assert.False(s.Board.Tissues.ContainsKey(offBoard));
            Assert.Equal(0, UltimatePolicy.ExcaliburScore(s, t.Position, offBoard));   // 读盘外不抛、记 0 分
        }
    }

    [Fact]
    public void Excalibur_30分_第12第13回合不放_第14第15回合减半就放()
    {
        AssertNoTierCasts(Board("excalibur_short"), "excalibur_short 第 12 回合");
        AssertNoTierCasts(Board("excalibur_short", AtRound(13)), "excalibur_short 第 13 回合");
        AssertAllTiers(Board("excalibur_short", AtRound(14)), "k=action+effector_target|act=effector|to=1,1|dir=0", "excalibur_short 第 14 回合");
        AssertAllTiers(Board("excalibur_short", AtRound(15)), "k=action+effector_target|act=effector|to=1,1|dir=0", "excalibur_short 第 15 回合");
    }

    /// <summary>东西两条各 4 格癌组织、都是 40 分：同分取规范序第一个子项 —— 西边（to=-1,1 排在 to=1,1 前面）。</summary>
    [Fact]
    public void Excalibur_两个方向同分_朝规范序第一个方向放()
    {
        var s = Board("excalibur_tie");
        var t = WorldLoader.CellOfSeat(s, 0);
        Assert.Equal(40, UltimatePolicy.ExcaliburScore(s, t.Position, WorldLoader.Pos("-1,1")));
        Assert.Equal(40, UltimatePolicy.ExcaliburScore(s, t.Position, WorldLoader.Pos("1,1")));
        AssertAllTiers(s, "k=action+effector_target|act=effector|to=-1,1|dir=3", "excalibur_tie");
    }

    /// <summary>
    /// 东边射线 3 格癌组织 + 旁边一格 (1,2)，它同时挨着射线上的 (1,1) 和 (2,1)：只算一次 ⇒ 30 + 6 = 36，差 4 分不到 40 ——
    /// 第 12 回合不放，第 14 回合放（算两次就是 42，第 12 回合就放了）。
    /// </summary>
    [Fact]
    public void Excalibur_侧向一格挨着两格射线_只算一次()
    {
        var s = Board("excalibur_splash");
        var t = WorldLoader.CellOfSeat(s, 0);
        Assert.Equal(36, UltimatePolicy.ExcaliburScore(s, t.Position, WorldLoader.Pos("1,1")));
        AssertNoTierCasts(s, "excalibur_splash 第 12 回合");
        AssertAllTiers(Board("excalibur_splash", AtRound(14)), "k=action+effector_target|act=effector|to=1,1|dir=0", "excalibur_splash 第 14 回合");
    }

    [Fact]
    public void Excalibur_第15回合六个方向全是0分_不放()
        => AssertNoTierCasts(Board("excalibur_empty"), "excalibur_empty");

    /// <summary>
    /// 漂移护栏：AI 自己走的射线 / 波及候选（<see cref="UltimatePolicy.ExcaliburArea"/>）与真引擎执行的结果对得上 ——
    /// 射线上被翻的格逐格相等（= 射线上的普通癌组织），射线外被翻的格都在候选里，40 个种子下来候选格个个都被掷中过（60% 一发，漏掉的概率 0.4^40）。
    /// 射线上站着的癌细胞都挨了打；候选格各算一次（不重复）；战果分 = 10 × 射线癌组织 + 6 × 候选 + 20 × 射线上的癌细胞。
    /// </summary>
    [Theory]
    [InlineData("excalibur_field")]
    [InlineData("excalibur_line")]
    [InlineData("excalibur_edge")]
    [InlineData("excalibur_splash")]
    public void Excalibur射线与波及预测_与引擎真执行一致(string board)
    {
        var s = Board(board);
        var t = WorldLoader.CellOfSeat(s, 0);
        var (_, options) = RolloutCursorProbe.NextAsked(s);
        var casts = options.OfType<TypeSkillDecision>().Where(d => d.Skill == "Excalibur").ToList();
        Assert.Equal(6, casts.Count);
        foreach (var cast in casts)
        {
            var (ray, splash) = UltimatePolicy.ExcaliburArea(s, t.Position, cast.Target!.Value);
            var rayCancer = ray.Where(p => s.Board.Tissues[p].State == TissueState.Cancer).ToHashSet();
            var rayCells = ray.Select(p => s.GetCellAt(p)).OfType<Cell>().Where(c => c.IsAlive && c.Faction == Faction.Cancer).ToList();
            var splashSet = splash.ToHashSet();
            Assert.True(splashSet.Count == splash.Count, $"{board} 朝 {cast.Target}：侧向候选有重复的格");
            Assert.Equal(10 * rayCancer.Count + 6 * splashSet.Count + 20 * rayCells.Count, UltimatePolicy.ExcaliburScore(s, t.Position, cast.Target!.Value));
            var splashSeen = new HashSet<HexPosition>();
            for (ulong seed = 1; seed <= 40; seed++)
            {
                var after = Engine.ExecuteDecision(s, cast, new Xoshiro256StarStar(seed));
                Assert.True(after.Success, after.ErrorMessage);
                var flipped = s.Board.Tissues.Keys
                    .Where(p => s.Board.Tissues[p].State == TissueState.Cancer && after.NewState.Board.Tissues[p].State == TissueState.Healthy).ToHashSet();
                Assert.True(rayCancer.SetEquals(flipped.Where(ray.Contains)), $"{board} 朝 {cast.Target}：射线上翻的格与预测不同");
                var offRay = flipped.Where(p => !ray.Contains(p)).ToList();
                Assert.True(offRay.All(splash.Contains), $"{board} 朝 {cast.Target}：翻了候选之外的格 {string.Join(" ", offRay.Where(p => !splash.Contains(p)))}");
                splashSeen.UnionWith(offRay);
                foreach (var c in rayCells)
                    Assert.True(after.NewState.Cells[c.Id].Energy < c.Energy, $"{board} 朝 {cast.Target}：射线上的癌细胞 {c.Id} 没挨打");
            }
            Assert.True(splashSeen.SetEquals(splash), $"{board} 朝 {cast.Target}：40 个种子下来掷中的侧向格与候选不同");
        }
    }

    // ============ 中和抗体 ============

    [Fact]
    public void 中和抗体_一个目标都没有_第15回合也不放()
        => AssertNoTierCasts(Board("neutralize_hidden"), "neutralize_hidden");

    [Fact]
    public void 中和抗体_一个光身目标第12回合不放_带一张永久卡就放()
    {
        AssertNoTierCasts(Board("neutralize_one"), "neutralize_one");
        AssertAllTiers(Board("neutralize_one", AtRound(14)), "k=action|act=effector", "neutralize_one 第 14 回合");
        AssertAllTiers(Board("neutralize_card"), "k=action|act=effector", "neutralize_card");
    }

    [Fact]
    public void 中和抗体_已经被中和的癌细胞不算分()
    {
        // 两个贴健康组织的目标，其中一个本回合已被中和 → 1 分：第 12 回合不放，第 14 回合放
        AssertNoTierCasts(Board("neutralize_already"), "neutralize_already");
        AssertAllTiers(Board("neutralize_already", AtRound(14)), "k=action|act=effector", "neutralize_already 第 14 回合");
    }

    [Fact]
    public void 中和抗体放完_同一回合接着打抗体()
    {
        var s = Board("neutralize_card");
        var normal = AiPolicies.Create(AiConfig.For(AiTier.Normal));
        var first = Answer(normal, s);
        Assert.Equal("k=action|act=effector", first);
        s = Execute(s, first, new Xoshiro256StarStar(1));
        Assert.Equal("k=action|act=antibody", Answer(normal, s));   // 护甲类被动已经关掉，这一发吃满
    }

    // ============ 免疫猎杀 ============

    [Fact]
    public void 免疫猎杀_打3格内队友最多且能加新标记的那个()
        // 席位 5：T、巨噬都在 3 格内 + 新标记 = 3 分；规范序第一个子项是席位 1（0 + 1 分）
        => AssertAllTiers(Board("hunt_allies"), "k=action+effector_target|act=effector|cid=5", "hunt_allies");

    [Fact]
    public void 免疫猎杀_树突光环里的目标不加新标记分()
        // 席位 1 在树突 2 格内：1 + 0 分（能量更低，光环不算的话它同分胜出）；席位 3：1 + 1 = 2 分
        => AssertAllTiers(Board("hunt_aura"), "k=action+effector_target|act=effector|cid=3", "hunt_aura");

    [Fact]
    public void 免疫猎杀_本回合已经标过的目标不加新标记分()
    {
        // hunt_aura 的席位 3 本回合已经得过标记：1 + 0 分，席位 1（光环里）也是 1 分 ⇒ 第 12 回合不放
        WorldSpec Marked(WorldSpec w) => w with { Cells = w.Cells.Select(c => c.Seat == 3 ? c with { Marked = true, MarkLeft = 1, MarkRound = w.Round } : c).ToList() };
        AssertNoTierCasts(Board("hunt_aura", Marked), "hunt_aura（席位 3 本回合已标过）");
    }

    [Fact]
    public void 免疫猎杀_同分打能量最低的()
        => AssertAllTiers(Board("hunt_tie"), "k=action+effector_target|act=effector|cid=3", "hunt_tie");

    [Fact]
    public void 免疫猎杀_同分同能量_取规范序第一个()
    {
        // hunt_tie 的席位 1 也调成 10.0 能量：两个目标都是 2 分、能量一样 ⇒ 规范序第一个子项 cid=1（不是后来的 cid=3）
        WorldSpec Even(WorldSpec w) => w with { Cells = w.Cells.Select(c => c.Seat == 1 ? c with { Energy = 100 } : c).ToList() };
        AssertAllTiers(Board("hunt_tie", Even), "k=action+effector_target|act=effector|cid=1", "hunt_tie（两个目标同能量）");
    }

    [Fact]
    public void 免疫猎杀_B细胞也算能出手的队友()
    {
        // hunt_aura 的 T 换成 B：席位 3 靠 3 格内的这只 B（1 分）+ 新标记（1 分）够到 2 分，第 12 回合就放
        WorldSpec WithB(WorldSpec w) => w with { Cells = w.Cells.Select(c => c.Seat == 2 ? c with { Type = "BCell" } : c).ToList() };
        AssertAllTiers(Board("hunt_aura", WithB), "k=action+effector_target|act=effector|cid=3", "hunt_aura（T 换成 B）");
    }

    [Fact]
    public void 免疫猎杀_最好只有1分_第12回合不放_第14回合放()
    {
        // 把 T 挪远：席位 3 只剩「新标记」1 分
        WorldSpec Far(WorldSpec w) => w with { Cells = w.Cells.Select(c => c.Seat == 2 ? c with { At = "-1,-5" } : c).ToList() };
        AssertNoTierCasts(Board("hunt_aura", Far), "hunt_aura（T 挪远）");
        AssertAllTiers(Board("hunt_aura", w => Far(w) with { Round = 14 }), "k=action+effector_target|act=effector|cid=3", "hunt_aura（T 挪远）第 14 回合");
    }

    // ============ 连续吞噬 ============

    /// <summary>
    /// 放 → 下一问净化能连跳最多的 (1,1)（普通档平时会挑癌性邻格更多的诱饵 (-1,1)，它旁边全是固化格、一跳都连不了）→ 三跳把整片吃完。
    /// 三档逐键相同：意图 / 搜索档也是先放、再净化（第 ③ 条在它们自己的评估之前先问），连跳一律由普通档答。
    /// </summary>
    [Fact]
    public void 连续吞噬_放完先净化连跳最多的那一格_连跳把整片吃完()
    {
        string[] expected =
        [
            "k=action|act=effector",
            "k=action|act=move|to=1,1",
            "k=free_move|g=连续吞噬|to=2,1",
            "k=free_move|g=连续吞噬|to=3,1",
            "k=free_move|g=连续吞噬|to=4,1",
        ];
        foreach (var tier in Tiers)
        {
            var policy = AiPolicies.Create(AiConfig.For(tier));
            var s = Board("chain_line");
            var macro = WorldLoader.CellOfSeat(s, 0).Id;
            var rng = new Xoshiro256StarStar(1);
            var keys = new List<string>();
            for (var step = 0; step < 10; step++)
            {
                var key = Answer(policy, s);
                keys.Add(key);
                s = Execute(s, key, rng);
                if (s.Turn.PendingChainCell is null && s.Cells[macro].ChainLeft < CellRules.ChainPhagoMax) break;
            }
            Assert.True(expected.SequenceEqual(keys), $"{tier} 档：{string.Join(" → ", keys)}");
            Assert.All(new[] { "1,1", "2,1", "3,1", "4,1" }, at => Assert.Equal(TissueState.Healthy, s.Board.Tissues[WorldLoader.Pos(at)].State));
            Assert.Equal(CellRules.ChainPhagoMax - 3, s.Cells[macro].ChainLeft);
        }
    }

    [Fact]
    public void 连续吞噬_能连两跳就放_只能连一跳第12回合不放第14回合放()
    {
        AssertAllTiers(Board("chain_two"), "k=action|act=effector", "chain_two");
        AssertNoTierCasts(Board("chain_one"), "chain_one");
        AssertAllTiers(Board("chain_one", AtRound(14)), "k=action|act=effector", "chain_one 第 14 回合");
    }

    [Fact]
    public void 连续吞噬_这一步没有能净化的格_第15回合也不放()
        => AssertNoTierCasts(Board("chain_blocked"), "chain_blocked");

    private static Func<WorldSpec, WorldSpec> MacroEnergy(int energy)
        => w => w with { Cells = w.Cells.Select(c => c.Seat == 0 ? c with { Energy = energy } : c).ToList() };

    /// <summary>
    /// 「付得起」与普通档步骤 7 同一把尺：账上要留得出「净化费 + 1.0」。chain_two 只差 0.5（走得进去、也活得下来）⇒ 不放；
    /// 正好够「净化费 + 1.0」⇒ 放。
    /// </summary>
    [Fact]
    public void 连续吞噬_付得起净化费但留不出1点0_不放()
    {
        var s0 = Board("chain_two");
        var cost = RulePolicies.QuoteMove(s0, WorldLoader.CellOfSeat(s0, 0), WorldLoader.Pos("1,1"))!.Value;
        AssertNoTierCasts(Board("chain_two", MacroEnergy(cost + 5)), "chain_two（只留得出 0.5）");
        AssertAllTiers(Board("chain_two", MacroEnergy(cost + 10)), "k=action|act=effector", "chain_two（正好留得出 1.0）");
    }

    /// <summary>chain_two 的第二跳 (3,1) 站着一个癌细胞：连跳只能落在没人的格 ⇒ 只剩 1 跳，第 12 回合不放、第 14 回合放。</summary>
    [Fact]
    public void 连续吞噬_第二跳的格有细胞占着_不算能跳()
    {
        WorldSpec Blocked(WorldSpec w) => w with { Cells = w.Cells.Select(c => c.Seat == 1 ? c with { At = "3,1" } : c).ToList() };
        AssertNoTierCasts(Board("chain_two", Blocked), "chain_two（(3,1) 有人）");
        AssertAllTiers(Board("chain_two", w => Blocked(w) with { Round = 14 }), "k=action|act=effector", "chain_two（(3,1) 有人）第 14 回合");
    }

    /// <summary>
    /// (1,1) 付得起、活得下来，但它后面那片的入口 (2,1) 回合末的压迫会把付完净化费的自己压死：不算能跳 ⇒ 0 跳，第 15 回合也不放。
    /// 「压死」按**付完净化费之后**的能量算：付完正好等于 (2,1) 的压迫 ⇒ 不放；多 0.1 ⇒ 放（按付之前的能量算，前一种也会放）。
    /// </summary>
    [Fact]
    public void 连续吞噬_能跳的格回合末都会压死自己_不算能跳_不放()
    {
        var s = Board("chain_deep");
        var macro = WorldLoader.CellOfSeat(s, 0);
        var cost = RulePolicies.QuoteMove(s, macro, WorldLoader.Pos("1,1"))!.Value;
        var afterPaying = macro.Energy - cost;
        var p11 = RulePolicies.PressureAt(s, WorldLoader.Pos("1,1"));
        var p21 = RulePolicies.PressureAt(s, WorldLoader.Pos("2,1"));
        Assert.True(afterPaying > p11 && afterPaying <= p21);
        AssertNoTierCasts(s, "chain_deep");

        Assert.True(p21 >= 10 && p21 > p11);   // 下面两种能量都过得了「净化费 + 1.0」与「走进 (1,1) 活得下来」，只差在 (2,1) 算不算能跳
        AssertNoTierCasts(Board("chain_deep", MacroEnergy(cost + p21)), "chain_deep（付完正好等于 (2,1) 的压迫）");
        AssertAllTiers(Board("chain_deep", MacroEnergy(cost + p21 + 1)), "k=action|act=effector", "chain_deep（付完比 (2,1) 的压迫多 0.1）");
    }

    /// <summary>
    /// 骨肉瘤【骨样硬化】标记过的癌组织：免疫走进去只登记蹲守、不净化，连锁也就挂不上（CellRules.Arrive）。
    /// 所以它既不能当连锁的起点，也不算能跳 —— 否则会放出一条一格都净化不了的空连锁。第 15 回合（门槛 1 跳）也不放。
    /// </summary>
    [Fact]
    public void 连续吞噬_骨样硬化标记格进去不净化_不当起点也不算能跳()
    {
        Func<WorldSpec, WorldSpec> Ossified(string at)
            => w => w with { Round = 15, Tiles = w.Tiles.Select(t => t.At == at ? t with { OssifyAt = 16 } : t).ToList() };
        AssertNoTierCasts(Board("chain_two", Ossified("1,1")), "chain_two（起点 (1,1) 有骨样硬化标记）");
        AssertNoTierCasts(Board("chain_two", Ossified("2,1")), "chain_two（第一跳 (2,1) 有骨样硬化标记）");
        AssertAllTiers(Board("chain_two", Ossified("3,1")), "k=action|act=effector", "chain_two（只有第二跳 (3,1) 有标记，还剩 1 跳）");
    }

    /// <summary>
    /// 「放完立刻去接连锁」只管放完之后那一问（额度还是满的 5）：连锁接过一段（还剩 2）之后，下一次净化不再抢在攻击前面 ——
    /// 三档共用的预检（<see cref="NormalPolicy.UltimateStep"/>）不接这一问，普通档照旧打旁边的癌细胞（走进它那一格）。
    /// 额度满 5 的同一块板三档都先去净化 (1,1)；(1,1) 有骨样硬化标记就不去（进去不净化）。
    /// </summary>
    [Fact]
    public void 连续吞噬_连锁接过一段之后_下一次净化不抢在攻击前面()
    {
        const string attack = "k=action|act=move|to=-1,1", purge = "k=action|act=move|to=1,1";
        var s = Board("chain_partial");
        Assert.Equal(attack, Answer(new NormalPolicy(lifecare: true, fixedLineup: false, castsUltimates: false), s));
        Assert.Null(UltimateStepKey(s));
        Assert.Equal(attack, Answer(AiTier.Normal, s));

        WorldSpec Fresh(WorldSpec w) => w with { Cells = w.Cells.Select(c => c.Seat == 0 ? c with { ChainLeft = CellRules.ChainPhagoMax } : c).ToList() };
        AssertAllTiers(Board("chain_partial", Fresh), purge, "chain_partial（额度满 5）");

        WorldSpec Marked(WorldSpec w) => Fresh(w) with { Tiles = w.Tiles.Select(t => t.At == "1,1" ? t with { OssifyAt = 14 } : t).ToList() };
        var marked = Board("chain_partial", Marked);
        Assert.Null(UltimateStepKey(marked));
        Assert.Equal(attack, Answer(AiTier.Normal, marked));
    }

    /// <summary>三档共用的大招预检这一问的答案（null = 不放、也不接连锁）。</summary>
    private static string? UltimateStepKey(WorldState s)
    {
        var (seat, options) = RolloutCursorProbe.NextAsked(s);
        return new NormalPolicy(AiConfig.For(AiTier.Normal)).UltimateStep(new AiView(s), AskView.Build(s, seat, options))?.Key;
    }

    /// <summary>连锁正在跑、唯一的下一跳 (1,1) 回合末的压迫正好等于自己的能量：不跳、停下；多 0.1 能量就跳。</summary>
    [Fact]
    public void 连续吞噬_连跳不跳进回合末会压死自己的格()
    {
        var pressure = RulePolicies.PressureAt(Board("chain_lethal"), WorldLoader.Pos("1,1"));
        Assert.True(pressure > 0);
        WorldState WithEnergy(int energy) => Board("chain_lethal", w => w with { Cells = w.Cells.Select(c => c.Seat == 0 ? c with { Energy = energy } : c).ToList() });
        foreach (var tier in Tiers)
        {
            Assert.Equal("k=free_move|g=连续吞噬|stop=1", Answer(tier, WithEnergy(pressure)));
            Assert.Equal("k=free_move|g=连续吞噬|to=1,1", Answer(tier, WithEnergy(pressure + 1)));
        }
    }

    /// <summary>同一个会压死自己的落点，换成卡牌给的免费移动（tag 不是「连续吞噬」）：照旧走进去 —— 卡牌的走法不变。</summary>
    [Fact]
    public void 卡牌给的免费移动_照旧走进那一格()
    {
        var s0 = Board("chain_lethal");
        var pressure = RulePolicies.PressureAt(s0, WorldLoader.Pos("1,1"));
        var s = Board("chain_lethal", w => w with { Cells = w.Cells.Select(c => c.Seat == 0 ? c with { Energy = pressure } : c).ToList() });
        var (seat, options) = RolloutCursorProbe.NextAsked(s);
        var chain = AskView.Build(s, seat, options);
        var card = new AskView { Seat = chain.Seat, Kind = chain.Kind, Tag = "炎症性趋化", Options = chain.Options };
        var policy = new NormalPolicy(AiConfig.For(AiTier.Normal));
        Assert.Equal("k=free_move|g=连续吞噬|stop=1", policy.Decide(new AiView(s), chain, 0).Key);
        Assert.Equal("k=free_move|g=连续吞噬|to=1,1", policy.Decide(new AiView(s), card, 0).Key);
    }

    // ============ 实局取样 ============

    /// <summary>
    /// 计数跑（<see cref="UltimateCastCount"/>，CW_AI_ULT_DUMP）里普通档门槛开着的真实局面，每种大招「放」「不放」各一块（6 人局），
    /// 外加【连续吞噬】刚放完、去接连锁的那一问：普通档答的就是录下来的那个键，三档放不放一致、放就同一个键。
    /// </summary>
    [Theory]
    [InlineData("real_excalibur_cast", "k=action+effector_target|act=effector|to=5,-1|dir=3")]
    [InlineData("real_excalibur_hold", "k=action|act=move|to=0,-3")]
    [InlineData("real_neutralize_cast", "k=action|act=effector")]
    [InlineData("real_neutralize_hold", "k=action|act=move|to=2,-5")]
    [InlineData("real_hunt_cast", "k=action+effector_target|act=effector|cid=1")]
    [InlineData("real_hunt_hold", "k=action|act=draw")]
    [InlineData("real_chain_cast", "k=action|act=effector")]
    [InlineData("real_chain_hold", "k=action|act=end")]
    [InlineData("real_chain_armed", "k=action|act=move|to=-3,-3")]
    public void 实局取样_普通档答录下来的键_三档放不放一致(string board, string recorded)
    {
        var s = Board(board);
        var (_, options) = RolloutCursorProbe.NextAsked(s);
        Assert.Contains(options, o => o is TypeSkillDecision { Skill: "Excalibur" or "中和抗体" or "免疫猎杀" or "连续吞噬" }
            || WorldLoader.CellOfSeat(s, s.Turn.ActivePlayerSeat).ChainLeft == CellRules.ChainPhagoMax);   // 门槛真开着
        Assert.Equal(recorded, Answer(AiTier.Normal, s));
        var armed = board == "real_chain_armed";
        if (IsCast(recorded) || armed) AssertAllTiers(s, recorded, board);
        else AssertNoTierCasts(s, board);
    }

    // ============ 结构 ============

    /// <summary>陪练（castsUltimates = false）在门槛开着、值得放的板上照旧作答，不放 —— 推演里谁都不放的前提。</summary>
    [Theory]
    [InlineData("excalibur_line")]
    [InlineData("neutralize_card")]
    [InlineData("hunt_allies")]
    [InlineData("chain_line")]
    public void 陪练不放大招_门槛开着也照旧作答(string board)
    {
        var s = Board(board);
        Assert.True(IsCast(Answer(AiTier.Normal, s)), $"{board} 本该是值得放的板");
        Assert.False(IsCast(Answer(new NormalPolicy(lifecare: true, fixedLineup: false, castsUltimates: false), s)));
    }

    /// <summary>
    /// 搜索档推演里替所有席位作答的陪练一次都不放：第 13 回合 30 分不放，推演跨进第 14 回合（门槛减半）后 T 的问答由陪练答 ——
    /// 那些问答里会放的普通档一定放（先核这一点，免得测了个空），陪练一条效应应答都不许出。
    /// </summary>
    [Fact]
    public void 搜索档推演里陪练一次都不放()
    {
        var s = Board("excalibur_short", AtRound(13));
        var seen = new List<(int Seat, string Key, WorldState State)>();
        RolloutCursor.DebugStep = (who, key, _, st) => seen.Add((who, key, st));
        string live;
        try { live = Answer(AiTier.Search, s); }
        finally { RolloutCursor.DebugStep = null; }
        Assert.False(IsCast(live));
        var casting = AiPolicies.Create(AiConfig.For(AiTier.Normal));
        var worth = seen.Count(x => x.Seat == 0 && x.State.Turn.WorldRound == 14 && IsCast(Answer(casting, x.State)));
        Assert.True(worth > 0, "推演里没有一问是会放的普通档会放的 —— 这条测试测了个空");
        Assert.DoesNotContain(seen, x => x.Key.Contains("effector", StringComparison.Ordinal));
    }

    /// <summary>门槛关着（记忆 19 / 本回合已放过）：效应应答不给，答案与不放大招的普通档逐字相同；三档都不放。</summary>
    [Theory]
    [InlineData("excalibur_line", "memory")]
    [InlineData("excalibur_line", "round")]
    [InlineData("chain_line", "memory")]
    [InlineData("hunt_allies", "round")]
    public void 门槛关着_答案与不放大招的普通档逐字相同(string board, string gate)
    {
        var s = Board(board, w => gate == "memory"
            ? w with { Players = w.Players.Select(p => p.Faction == "immune" ? p with { Memory = 19 } : p).ToList() }
            : w with { EffectorRound = w.Round });
        var (seat, options) = RolloutCursorProbe.NextAsked(s);
        Assert.DoesNotContain(options, o => o is TypeSkillDecision { Skill: "Excalibur" or "中和抗体" or "免疫猎杀" or "连续吞噬" });
        Assert.Equal(Answer(new NormalPolicy(true, false, castsUltimates: false), s), Answer(AiTier.Normal, s));
        AssertNoTierCasts(s, $"{board}（{gate} 闸关着）");
    }

    /// <summary>纯函数：同一局面两遍同一答案；搜索档给不给预算，放的都一样（在开搜之前就定了，不占预算）。</summary>
    [Theory]
    [InlineData("excalibur_line")]
    [InlineData("hunt_allies")]
    [InlineData("chain_line")]
    public void 同一局面两遍同一答案_搜索档给不给预算放的都一样(string board)
    {
        var s = Board(board);
        foreach (var tier in Tiers)
            Assert.Equal(Answer(tier, s), Answer(tier, s));
        var budgeted = Answer(AiPolicies.Create(new AiConfig { Tier = AiTier.Search, BudgetMs = 2500 }), s);
        Assert.Equal(Answer(AiTier.Search, s), budgeted);
        Assert.True(IsCast(budgeted));
    }

    /// <summary>
    /// 对拍语料里一问效应应答都没有（录到的局面到不了 X 级）：所以三份语料对这一支零覆盖、加大招之后照样全绿。
    /// 谁拿 GD 当老师重录、录到了 X 级，这里先红 —— GD 不放大招，语料会在第一问效应应答上与 C# 分叉（NormalPolicy 头注 ③）。
    /// </summary>
    [Fact]
    public void 对拍语料里一问效应应答都没有()
    {
        foreach (var file in new[] { "agree_2p.jsonl.gz", "agree_4p.jsonl.gz", "agree_6p.jsonl.gz" })
        {
            var asks = AgreeReplay.ReadRows(AgreementTests.Corpus(file)).Where(r => r.GetProperty("t").GetString() == "ask").ToList();
            Assert.True(asks.Count > 100, $"{file} 只有 {asks.Count} 问");
            var effector = asks.Count(r => r.GetProperty("kind").GetString() == "effector_target"
                || r.GetProperty("opts").EnumerateArray().Any(o => o.GetProperty("k").GetString()!.Contains("act=effector", StringComparison.Ordinal)));
            Assert.Equal(0, effector);
        }
    }
}
