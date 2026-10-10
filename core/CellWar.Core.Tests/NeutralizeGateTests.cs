using CellWar.Core.Worlds;

namespace CellWar.Core.Tests;

/// <summary>
/// Kevin 10-09（`docs/内核替换_重启计划.md` §八 第 9 条）：C# 内核两处与 GD 不一致，按 GD 修 ——
/// ①【中和抗体】一个目标都没有时 GD 不给按钮（cw_actions.gd `_effector_ready` → `_neutralize_targets`），C# 照给、点了白扣 20 效应记忆；
/// ② 被【中和抗体】压着的癌细胞，四个种类主动技能 GD 一个都不给（`_type_options` 开头的 `type_ability_on`），C# 照给。
///
/// 两侧同跑的那半在 L0：`game/tests/l0/batch5c/build_options.json`（探针 `build_options` 数选项表里的按钮）。
/// 这里补 L0 碰不到的那半：**按钮不在，硬交上来也驳回** —— 不扣记忆、不扣能量、不动盘面（Dispatch 先 Validate）。
/// </summary>
public class NeutralizeGateTests
{
    private static readonly BasicRulesEngine Engine = new();

    // ---------- ①【中和抗体】没有目标就不给 ----------

    /// <summary>
    /// 唯一的癌细胞（骨肉瘤）站在 (1,-1)。<paramref name="healthyNeighbor"/> = false 时脚下与六邻全铺癌组织（零个目标），
    /// true 时把 (0,0) 留成健康组织（一个目标）。B 细胞的门槛全开：X 级、已分化、记忆 25 ≥ 20、本回合没放过。
    /// </summary>
    private static WorldState EffectorWorld(bool healthyNeighbor, int? cancerNeutralUntil = null)
    {
        var cancerTiles = new List<string> { "1,-1", "2,-1", "2,-2", "1,-2", "0,-1", "1,0" };
        if (!healthyNeighbor) cancerTiles.Add("0,0");
        var tiles = string.Join(", ", cancerTiles.Select(t => $$"""{ "at": "{{t}}", "state": "cancer" }"""));
        var neutral = cancerNeutralUntil is { } until ? $", \"neutral_until\": {until}" : "";
        return WorldLoader.Load(WorldJson.Parse($$"""
        {
          "round": 3,
          "players": [ { "seat": 0, "faction": "immune", "level": "X", "memory": 25 }, { "seat": 1, "faction": "cancer", "cancer_type": "Osteosarcoma" } ],
          "tiles": [ {{tiles}} ],
          "cells": [
            { "seat": 0, "type": "BCell", "at": "-4,1", "energy": 300, "differentiated": true },
            { "seat": 1, "type": "Osteosarcoma", "at": "1,-1", "energy": 300{{neutral}} }
          ]
        }
        """));
    }

    private static Cell OfSeat(WorldState s, int seat) => s.Cells.Values.Single(c => c.OwnerSeat == seat);

    private static bool Offers(WorldState s, int seat, string skill)
        => Engine.GetAvailableDecisions(s, seat).Any(d => d is TypeSkillDecision t && t.Skill == skill);

    [Fact]
    public void 中和抗体_一个目标都没有_不给按钮_硬交上来也驳回且不扣记忆()
    {
        var s = EffectorWorld(healthyNeighbor: false);
        var b = OfSeat(s, 0);
        Assert.False(Offers(s, 0, "中和抗体"), "没有与健康组织相邻的癌细胞：GD `_effector_ready` 不出这个按钮");

        var cast = new TypeSkillDecision(0, b.Id, "中和抗体");
        Assert.False(Engine.ValidateDecision(s, cast).IsValid);
        var r = Engine.ExecuteDecision(s, cast, new Xoshiro256StarStar(1));
        Assert.False(r.Success, "提交时也要复验（GD `_do_effector` 开头再查一遍 `_effector_ready`）");
        Assert.Equal(25, r.NewState.Players[0].AntigenMemory);   // 白扣 20 记忆正是 10-09 查出来的那条
        Assert.False(r.NewState.Cells[b.Id].EffectorUsed);
        Assert.NotEqual(s.Turn.WorldRound, r.NewState.Turn.EffectorRound);
    }

    [Fact]
    public void 中和抗体_有一个目标就给_放了压住它()
    {
        var s = EffectorWorld(healthyNeighbor: true);
        var b = OfSeat(s, 0);
        var cancer = OfSeat(s, 1);
        Assert.True(Offers(s, 0, "中和抗体"));

        var r = Engine.ExecuteDecision(s, new TypeSkillDecision(0, b.Id, "中和抗体"), new Xoshiro256StarStar(1));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(5, r.NewState.Players[0].AntigenMemory);
        Assert.Equal(s.Turn.WorldRound + 1, r.NewState.Cells[cancer.Id].NeutralUntil);
    }

    /// <summary>GD `_neutralize_targets` 不筛已经被中和的：唯一的目标本回合已被压着，按钮照给（别按 AI 放法的打分口径改规则）。</summary>
    [Fact]
    public void 中和抗体_目标已经被中和也算目标_按钮照给()
    {
        var s = EffectorWorld(healthyNeighbor: true, cancerNeutralUntil: 3);
        Assert.True(RulePolicies.Neutralized(s, OfSeat(s, 1)));
        Assert.True(Offers(s, 0, "中和抗体"));
    }

    // ---------- ② 被中和的癌细胞，四个种类主动技能一个都不给 ----------

    /// <summary>
    /// 癌席（席位 1）的行动回合，癌细胞站在一格癌组织上、能量 30.0，各自的技能前提都满足：
    /// 印戒能量 ≥ 2.0；骨肉瘤脚下是没标过的普通癌组织；黑色素瘤站在血管 (6,0)；小细胞在 (0,0)、六个方向各跃进 5 格都落在空格上。
    /// </summary>
    private static WorldState CancerWorld(string type, string at, int round, int? neutralUntil)
    {
        var neutral = neutralUntil is { } until ? $", \"neutral_until\": {until}" : "";
        return WorldLoader.Load(WorldJson.Parse($$"""
        {
          "round": {{round}}, "seat": 1,
          "players": [ { "seat": 0, "faction": "immune", "level": "I" }, { "seat": 1, "faction": "cancer", "cancer_type": "{{type}}" } ],
          "tiles": [ { "at": "{{at}}", "state": "cancer" } ],
          "cells": [
            { "seat": 0, "type": "ImmuneBasic", "at": "-4,1", "energy": 300 },
            { "seat": 1, "type": "{{type}}", "at": "{{at}}", "energy": 300{{neutral}} }
          ]
        }
        """));
    }

    public static TheoryData<string, string, string> TypeSkills => new()
    {
        { "SignetRing", "1,-1", "黏液破裂" },
        { "Osteosarcoma", "1,-1", "骨样硬化" },
        { "Melanoma", "6,0", "早期血行转移" },
        { "SmallCellLung", "0,0", "转移" },
    };

    [Theory]
    [MemberData(nameof(TypeSkills))]
    public void 被中和的癌细胞_种类主动技能一条都不给_硬交上来也驳回(string type, string at, string skill)
    {
        // 对照：同一盘面没被中和时这个技能是给的（盘面确实满足前提，下面的「不给」不是蒙对）
        var free = CancerWorld(type, at, round: 4, neutralUntil: null);
        var offered = Engine.GetAvailableDecisions(free, 1).OfType<TypeSkillDecision>().Where(d => d.Skill == skill).ToList();
        Assert.NotEmpty(offered);

        // neutral_until = 4、现在第 4 回合：末回合仍压着（`round <= neutral_until`）
        var held = CancerWorld(type, at, round: 4, neutralUntil: 4);
        Assert.False(Offers(held, 1, skill), $"被中和的 {type} 不该有【{skill}】（GD `_type_options` 开头的 `type_ability_on`）");
        foreach (var d in offered)
            Assert.False(Engine.ValidateDecision(held, d).IsValid, $"{SemanticKey.Of(held, d)} 硬交上来也要驳回");

        var cell = OfSeat(held, 1);
        var r = Engine.ExecuteDecision(held, offered[0], new Xoshiro256StarStar(1));
        Assert.False(r.Success);
        Assert.Equal(cell.Energy, r.NewState.Cells[cell.Id].Energy);
        Assert.Equal(cell.Position, r.NewState.Cells[cell.Id].Position);
        Assert.True(r.NewState.Cells[cell.Id].IsAlive);   // 【黏液破裂】是自毁技能：驳回了就还活着
    }

    [Theory]
    [MemberData(nameof(TypeSkills))]
    public void 中和过期之后_种类主动技能照给(string type, string at, string skill)
    {
        var s = CancerWorld(type, at, round: 5, neutralUntil: 4);   // 第 4 回合末就解除了
        Assert.False(RulePolicies.Neutralized(s, OfSeat(s, 1)));
        Assert.True(Offers(s, 1, skill));
    }
}
