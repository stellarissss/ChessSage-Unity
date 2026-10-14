using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Cheat
{
    /// <summary>
    /// 作弊改规面板的纯数据模型（零引擎依赖，可在 dotnet / EditMode 下直接测试）。
    ///
    /// 【分层意图】表现层（UGUI）只负责"把这里的数据画出来 + 收玩家输入"，
    /// 所有判断（diff 人读化、代价分档、失败文案映射、预置补丁）都留在 Core，
    /// 从而既可回归测试，又保证与 Core 引擎语义永远一致。
    /// </summary>

    /// <summary>「作弊张力」的风险档位（UX §1.4 / §8.2）。</summary>
    public enum CheatTier
    {
        /// <summary>不溢出：只消耗燃料业力，不触发识破结算。绿色。</summary>
        Safe,
        /// <summary>临界：小幅溢出（O&lt;30），识破增量小。琥珀色。</summary>
        Critical,
        /// <summary>危险：大幅溢出（O≥30），识破风险高，强制二次确认。红色。</summary>
        Danger,
    }

    /// <summary>失败反馈里可点的"出路"按钮类型（UX §7.1）。</summary>
    public enum CheatActionKind
    {
        None,
        /// <summary>采用系统给的具体建议（回填输入框建议文本）。</summary>
        ApplySuggestion,
        /// <summary>手动修改（把玩家原指令回填输入框）。</summary>
        ManualEdit,
        /// <summary>前往技能树。</summary>
        GoSkillTree,
        /// <summary>拆成两小步。</summary>
        SplitSteps,
        /// <summary>重新施展（重试）。</summary>
        Retry,
        /// <summary>改用预置指令（降级）。</summary>
        UsePreset,
        /// <summary>换个说法。</summary>
        ChangeWording,
        /// <summary>打开预置指令。</summary>
        OpenPresets,
        /// <summary>重新连接天道。</summary>
        Reconnect,
    }

    /// <summary>一条「改前 → 改后」的对照行。</summary>
    public sealed class DiffLine
    {
        public string Label;
        public string Before;
        public string After;

        public DiffLine() { }

        public DiffLine(string label, string before, string after)
        {
            Label = label;
            Before = before;
            After = after;
        }
    }

    /// <summary>一张人读 diff 卡片（对应一处规则/棋子/棋盘的改动）。</summary>
    public sealed class DiffCard
    {
        public string Title;
        public readonly List<DiffLine> Lines = new List<DiffLine>();
        public string Impact;
        /// <summary>partial 场景下：该项无法实现。</summary>
        public bool Failed;
        public string FailReason;

        public bool HasChanges => Lines.Count > 0;
    }

    /// <summary>代价预估（UX §8）——全部在"确认之前"算清。</summary>
    public sealed class CostPreview
    {
        /// <summary>本手业力代价 C（引擎返回 estimated_karma_cost 或预置价目）。</summary>
        public int Cost;
        /// <summary>施展前业力 K。</summary>
        public int Karma;
        public int KarmaMax;
        /// <summary>溢出量 O = max(0, K + C − K_max)。</summary>
        public int Overshoot;
        /// <summary>识破增量 Δ（百分点），O≤0 时为 0。</summary>
        public double DetectionDelta;
        /// <summary>施展后识破概率（截断 100）。</summary>
        public double DetectionAfter;
        /// <summary>识破是否已锁死（被识破后不再累积）。</summary>
        public bool DetectionLocked;
        public CheatTier Tier;
        /// <summary>技能/道加成标注（如「『效率欺诈』生效」），无则空。</summary>
        public string SkillNote;
        /// <summary>本手免费（『白嫖』）。</summary>
        public bool FreeCheat;

        public int KarmaAfter => Karma + Cost;
        public bool IsOverdraft => Overshoot > 0;
    }

    /// <summary>一个可点的出路按钮。</summary>
    public sealed class CheatAction
    {
        public string Label;
        public CheatActionKind Kind;
        /// <summary>采用建议/手动修改时要回填输入框的文本（可空）。</summary>
        public string FillText;

        public CheatAction() { }

        public CheatAction(string label, CheatActionKind kind, string fillText = null)
        {
            Label = label;
            Kind = kind;
            FillText = fillText;
        }
    }

    /// <summary>失败反馈（UX §7）：必须是「为什么不行 + 怎么改能行」，不是"非法指令"四个字。</summary>
    public sealed class CheatFailure
    {
        public string Title;
        public string Reason;
        public readonly List<CheatAction> Actions = new List<CheatAction>();
        /// <summary>E 类闲聊：用普通对话气泡显示，不弹预览。</summary>
        public bool IsFun;
        /// <summary>需要转入离线降级模式（无 API Key）。</summary>
        public bool Offline;
    }

    /// <summary>
    /// 一次「施展」的预览产物：要么可确认（Ok=true 且 Cards 非空），要么是一条失败/对话。
    /// </summary>
    public sealed class CheatPreview
    {
        /// <summary>是否进入预览态（有可确认的改写）。</summary>
        public bool Ok;
        /// <summary>引擎结果类型：applied/partial/success/rejected/fun/error/undo/preset。</summary>
        public string Type;
        public string Classification;
        /// <summary>引擎给玩家的原文（成功时的 response_to_player）。</summary>
        public string Message;

        /// <summary>改规快照（可空：E 类/fun/失败时为 null）。预览态下**绝不**写回对局。</summary>
        public JObject ModifiedConfigs;
        public readonly List<DiffCard> Cards = new List<DiffCard>();
        /// <summary>代价预估（可空）。</summary>
        public CostPreview CostInfo;
        /// <summary>失败/对话信息（非 Ok 时）。</summary>
        public CheatFailure Failure;

        /// <summary>partial：部分成功提示前缀。</summary>
        public bool Partial;
        public string PartialNote;
        /// <summary>悔棋类预置：确认时执行 UndoLastMove（不入改规栈）。</summary>
        public bool UndoMove;
    }

    /// <summary>确认施展后的结果（供 HUD 做变化反馈）。</summary>
    public sealed class CheatConfirmResult
    {
        public bool Applied;
        public int KarmaBefore;
        public int KarmaAfter;
        public int Cost;
        public int Overshoot;
        public double DetectionDelta;
        public double DetectionAfter;
        /// <summary>是否被识破（天道审判）。</summary>
        public bool Detected;
        public bool Escaped;
        /// <summary>识破判词（detected 时）。</summary>
        public string Judgment;
        public string Message;
        /// <summary>改规栈深度（撤回用）。</summary>
        public int RuleUndoDepth;
    }

    /// <summary>一条离线预置指令（UX §10.3）：绕过 LLM，直接命中预校验过的补丁模板。</summary>
    public sealed class PresetCommand
    {
        public string Text;
        /// <summary>适用棋种（"common" 表示全棋种通用）。</summary>
        public string Variant;
        public string Classification;
        public int Karma;
        /// <summary>改写的目标配置键（pieces_red/pieces_black/pieces_white/board_state/ui_config）。</summary>
        public string TargetConfig;
        /// <summary>RFC6902 补丁（应用到 TargetConfig 的当前配置）。</summary>
        public JArray Patch;
        /// <summary>悔棋类：确认时执行 UndoLastMove，不产生配置补丁。</summary>
        public bool IsUndoMove;

        public bool AppliesTo(string variant)
            => Variant == "common" || Variant == variant;
    }
}
