using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChessSage.Core.Config;
using ChessSage.Core.Game;
using ChessSage.Core.Json;
using ChessSage.Core.Model;
using ChessSage.Core.Orchestration;
using ChessSage.Core.Samsara;
using Newtonsoft.Json.Linq;

namespace ChessSage.Core.Cheat
{
    /// <summary>
    /// 作弊改规的**协调器**（Core 层，零引擎依赖）。它是「面板视图」与「三套引擎」之间的唯一黏合点：
    ///   · <see cref="OrchestratorEngine"/>：自然语言 → 意图/补丁/预估代价（**只预估，不改状态**）；
    ///   · <see cref="SamsaraEngine"/>：真正的业力/识破状态机（**扣费与结算在这里**）；
    ///   · <see cref="GameSession"/>：当前对局（配置快照 + 应用改规）。
    ///
    /// 【关键集成顺序 · UX §11.1】提交只做预估、不动业力；**扣费与识破结算必须发生在玩家"确认"之后**：
    ///   1. 同步预估器状态 → 2. 提交预估 → 3. 进预览（什么都没变）
    ///   → 4. 确认：入改规栈 → 应用配置 → IncreaseKarma → 溢出则 HandleOverdraft → 5. 回同步预估器。
    /// 顺序错误会产生两类经典 bug：先扣费再预览（取消后业力白扣）、不同步预估器（门控/预估全错）。
    /// </summary>
    public sealed class CheatController
    {
        readonly GameSession _session;
        readonly SamsaraEngine _samsara;
        readonly OrchestratorEngine _engine;
        readonly CheatRuleStack _rules = new CheatRuleStack();
        readonly CheatHistory _history = new CheatHistory();

        /// <summary>离线降级：无 API Key / 断网 / 连续失败时置真，改用预置指令集（UX §10）。</summary>
        public bool Offline { get; private set; }

        /// <summary>连续失败次数（≥2 自动引导预置指令，UX §4.2）。</summary>
        public int ConsecutiveFailures { get; private set; }

        public CheatController(GameSession session, SamsaraEngine samsara, OrchestratorEngine engine)
        {
            _session = session;
            _samsara = samsara;
            _engine = engine;
            Offline = engine == null || !engine.Client.IsAvailable;
        }

        public GameSession Session => _session;
        public CheatHistory History => _history;
        public int RuleUndoDepth => _rules.Count;
        public bool HasRuleUndo => _rules.CanUndo;

        /// <summary>是否可提交（非 AI 回合、对局未结束、有会话）。</summary>
        public bool CanSubmit => _session != null && !_session.IsEnded && !_session.IsAiTurn;

        /// <summary>AI 回合中无法改规（UX §4.3.3）。</summary>
        public bool AiTurnBlocked => _session != null && !_session.IsEnded && _session.IsAiTurn;

        /// <summary>手动进入/退出离线模式（试玩取证与无网环境用）。</summary>
        public void SetOffline(bool offline) => Offline = offline;

        /// <summary>重新连接天道（UX §10.4.3）：清失败计数、按密钥可用性重判离线。</summary>
        public void Reconnect()
        {
            ConsecutiveFailures = 0;
            Offline = _engine == null || !_engine.Client.IsAvailable;
        }

        // ── 状态读取（供 HUD）──────────────────────────────────────────

        public int Karma => _samsara?.GetKarma() ?? 0;
        public int KarmaMax => _samsara?.GetKarmaMax() ?? 120;
        public int KarmaSingleMax => _samsara?.GetKarmaSingleMax() ?? 120;
        public double Detection => _samsara?.GetDetection() ?? 0.0;
        public bool DetectionLocked => _samsara?.IsDetectionLocked() ?? false;

        public JObject GetHudState() => _samsara?.GetState() ?? new JObject();
        public JObject GetObjective() => _samsara?.GetCurrentObjective() ?? new JObject();
        public JObject SkillModifiers() => _samsara?.GetSkillModifiers() ?? new JObject();

        /// <summary>把真实 RPG 状态同步进编排层预估器（§11.1 步骤 1/5）。</summary>
        public void SyncAssessor()
        {
            if (_engine == null || _samsara == null) return;
            _engine.KarmaAssessor.SetLocalKarmaState(Karma, KarmaMax, KarmaSingleMax);
            _engine.KarmaAssessor.SetRealmDetection(Detection, _samsara.GetCurrentRealm());
        }

        // ── 提交（预估，不改状态）──────────────────────────────────────

        /// <summary>提交自然语言指令。离线时直接返回离线引导。</summary>
        public async Task<CheatPreview> SubmitAsync(string command, CancellationToken ct = default)
        {
            if (_session == null)
                return ErrorPreview("没有进行中的对局");

            if (Offline || _engine == null || !_engine.Client.IsAvailable)
                return OfflinePreview();

            var text = (command ?? "").Trim();
            if (text.Length == 0)
                return InfoPreview("先说一句你想改什么");

            SyncAssessor();
            var oldConfigs = BuildConfigs();
            var context = new JObject
            {
                ["configs"] = oldConfigs,
                ["skill_modifiers"] = SkillModifiers(),
                ["allowed_classifications"] = AllowedClassifications(),
            };

            JObject result;
            try
            {
                result = await _engine.ProcessCommandAsync(text, context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ConsecutiveFailures++;
                return ErrorPreview("请求超时或被取消");
            }
            catch (Exception e)
            {
                ConsecutiveFailures++;
                return ErrorPreview($"意图解析失败: {e.Message}");
            }

            var preview = ConvertResult(result, oldConfigs);
            var type = preview?.Type ?? "";
            if (type == "applied" || type == "partial" || type == "fun")
                ConsecutiveFailures = 0;
            else
                ConsecutiveFailures++;

            if (ConsecutiveFailures >= 2) Offline = true;
            if (preview != null && preview.Ok) _history.Add(text);
            return preview;
        }

        /// <summary>提交离线预置指令：绕过 LLM，直接生成补丁快照，仍走预览/确认/代价链路。</summary>
        public CheatPreview SubmitPreset(PresetCommand preset)
        {
            if (_session == null || preset == null) return ErrorPreview("没有进行中的对局");

            var oldConfigs = BuildConfigs();

            if (preset.IsUndoMove)
            {
                var undo = new CheatPreview
                {
                    Ok = true,
                    Type = "undo",
                    Classification = preset.Classification,
                    Message = "悔一步棋",
                    UndoMove = true,
                };
                var card = new DiffCard { Title = "悔棋", Impact = "回退一手棋（不入改规栈）" };
                card.Lines.Add(new DiffLine("棋局", "当前", "回退一手"));
                undo.Cards.Add(card);
                undo.CostInfo = BuildCost(preset.Karma);
                return undo;
            }

            JObject newCfg;
            try
            {
                var oldCfg = oldConfigs[preset.TargetConfig] as JObject ?? new JObject();
                newCfg = JsonPatch.Apply(oldCfg, preset.Patch) as JObject;
            }
            catch (Exception e)
            {
                return ErrorPreview($"预置补丁应用失败: {e.Message}");
            }
            if (newCfg == null) return ErrorPreview("预置补丁应用失败");

            var modified = new JObject { [preset.TargetConfig] = newCfg };
            var preview = new CheatPreview
            {
                Type = "preset",
                Classification = preset.Classification,
                Message = preset.Text,
                ModifiedConfigs = modified,
            };
            preview.Cards.AddRange(CheatDiffRenderer.Render(oldConfigs, modified));
            preview.Ok = preview.Cards.Count > 0;
            if (!preview.Ok)
            {
                preview.Failure = new CheatFailure
                {
                    Title = "天道觉得这和你现在的棋规没什么两样",
                    Reason = "这条预置指令没有产生变化。",
                };
                return preview;
            }
            preview.CostInfo = BuildCost(preset.Karma);
            _history.Add(preset.Text);
            return preview;
        }

        // ── 确认（唯一扣费点）─────────────────────────────────────────

        /// <summary>玩家确认施展：入改规栈 → 应用配置 → 扣业力 → 溢出则结算识破。</summary>
        public CheatConfirmResult Confirm(CheatPreview preview)
        {
            var res = new CheatConfirmResult { Applied = false };
            if (_session == null || preview == null || !preview.Ok) return res;

            var cost = preview.CostInfo?.Cost ?? 0;
            res.KarmaBefore = Karma;
            res.Cost = cost;

            // a. 记录改规快照（旧配置入改规栈，不含 board_state）
            if (!preview.UndoMove)
                _rules.Push(BuildRuleSlice(), _session.State.MoveHistory.Count);

            // b. 应用改规（棋局栈不动）
            if (!preview.UndoMove && preview.ModifiedConfigs != null)
                _session.ApplyConfigUpdate(preview.ModifiedConfigs);
            else if (preview.UndoMove)
                _session.UndoLastMove();

            // c. 扣业力（真正的扣费点）
            var inc = _samsara.IncreaseKarma(cost);
            res.KarmaAfter = Karma;
            res.Overshoot = (int)inc.Item3;

            // d. 溢出 → 结算识破
            if (inc.Item2)
            {
                var det = _samsara.Detection.HandleOverdraft(inc.Item3);
                res.Detected = GetBool(det, "detected");
                res.Escaped = GetBool(det, "escaped");
                res.DetectionDelta = GetDouble(det, "delta");
                res.DetectionAfter = GetDouble(det, "current");
                if (res.Detected) res.Judgment = _samsara.Detection.GetJudgmentText();
                else res.Judgment = GetStr(det, "message");
            }

            // e. 回同步预估器
            SyncAssessor();
            res.Applied = true;
            res.Message = preview.Message;
            res.RuleUndoDepth = _rules.Count;
            return res;
        }

        // ── 撤回改规（独立于棋局悔棋）──────────────────────────────────

        /// <summary>「改规后是否已走子」——撤回前需提示局面可能不一致（UX §6.2.3）。</summary>
        public bool NeedsMismatchWarning
        {
            get
            {
                var e = _rules.Peek();
                if (e == null || _session == null) return false;
                return _session.State.MoveHistory.Count > e.MoveCount;
            }
        }

        /// <summary>撤回最近一次改规：只回退规则，不回退棋谱，不返还业力。</summary>
        public bool UndoRule(out JObject slice)
        {
            slice = null;
            var e = _rules.Pop();
            if (e == null) return false;
            slice = e.Slice;
            if (_session != null) _session.ApplyConfigUpdate(e.Slice);
            return true;
        }

        // ── 上下文组装（§11 Core 接口映射表）─────────────────────────

        /// <summary>构建 <c>context["configs"]</c>：键与 ApplyConfigUpdate 的路由一一对应。</summary>
        public JObject BuildConfigs()
        {
            var cfg = new JObject();
            if (_session == null) return cfg;
            var c = _session.Config;
            cfg["board_state"] = _session.State.ToJson();
            if (c.Board != null) cfg["board"] = c.Board.DeepClone();
            if (c.Rules != null) cfg["rules"] = c.Rules.DeepClone();
            if (c.UiConfig != null) cfg["ui_config"] = c.UiConfig.DeepClone();
            PutPieceKeys(cfg, c);
            return cfg;
        }

        /// <summary>改规配置切片（**不含 board_state**，保证撤回改规不回退棋谱）。</summary>
        JObject BuildRuleSlice()
        {
            var slice = new JObject();
            if (_session == null) return slice;
            var c = _session.Config;
            if (c.Board != null) slice["board"] = c.Board.DeepClone();
            if (c.Rules != null) slice["rules"] = c.Rules.DeepClone();
            if (c.UiConfig != null) slice["ui_config"] = c.UiConfig.DeepClone();
            PutPieceKeys(slice, c);
            return slice;
        }

        static void PutPieceKeys(JObject target, GameConfig c)
        {
            if (c.IsRedBlack)
            {
                if (c.PiecesA != null) target["pieces_red"] = c.PiecesA.DeepClone();
                if (c.PiecesB != null) target["pieces_black"] = c.PiecesB.DeepClone();
            }
            else
            {
                if (c.PiecesA != null) target["pieces_black"] = c.PiecesA.DeepClone();
                if (c.PiecesB != null) target["pieces_white"] = c.PiecesB.DeepClone();
            }
        }

        JArray AllowedClassifications()
        {
            var arr = new JArray();
            if (_samsara == null) return arr;
            foreach (var c in _samsara.GetAllowedClassifications()) arr.Add(c);
            return arr;
        }

        // ── 结果转换 ──────────────────────────────────────────────────

        CheatPreview ConvertResult(JObject result, JObject oldConfigs)
        {
            var type = GetStr(result, "type");
            var preview = new CheatPreview
            {
                Type = type,
                Classification = GetStr(result, "classification"),
                Message = GetStr(result, "message"),
            };

            if (type == "applied" || type == "partial")
            {
                var modified = result["modified_configs"] as JObject;
                if (modified != null && modified.HasValues)
                {
                    preview.ModifiedConfigs = modified;
                    preview.Cards.AddRange(CheatDiffRenderer.Render(oldConfigs, modified));
                    if (preview.Cards.Count == 0)
                    {
                        preview.Failure = new CheatFailure
                        {
                            Title = "天道觉得这和你现在的棋规没什么两样",
                            Reason = string.IsNullOrEmpty(preview.Message) ? "没有产生实质变化。" : preview.Message,
                        };
                        return preview;
                    }
                    preview.Ok = true;
                    if (type == "partial")
                    {
                        preview.Partial = true;
                        preview.PartialNote = "本指令部分项无法实现，已为你保留可行部分";
                    }
                    preview.CostInfo = BuildCost(JsonHelpers.GetInt(result, "estimated_karma_cost"));
                    return preview;
                }
                preview.Failure = new CheatFailure
                {
                    Title = "天道觉得这和你现在的棋规没什么两样",
                    Reason = string.IsNullOrEmpty(preview.Message) ? "没有产生实质变化。" : preview.Message,
                };
                return preview;
            }

            preview.Failure = CheatErrorTranslator.Present(result);
            return preview;
        }

        CostPreview BuildCost(int cost)
            => CheatCostCalculator.Compute(cost, Karma, KarmaMax, SkillModifiers(), Detection, DetectionLocked);

        // ── 便捷构造 ──────────────────────────────────────────────────

        static CheatPreview OfflinePreview()
        {
            var f = new CheatFailure
            {
                Offline = true,
                Title = "🔌 离线模式：无法连接天道",
                Reason = "只能使用预置的改规。",
            };
            f.Actions.Add(new CheatAction("打开预置指令", CheatActionKind.OpenPresets));
            return new CheatPreview { Type = "offline", Failure = f };
        }

        static CheatPreview ErrorPreview(string message)
        {
            var result = new JObject { ["type"] = "error", ["message"] = message };
            return new CheatPreview { Type = "error", Failure = CheatErrorTranslator.Present(result) };
        }

        static CheatPreview InfoPreview(string message)
            => new CheatPreview
            {
                Type = "info",
                Failure = new CheatFailure { Title = "天道静候", Reason = message },
            };

        static string GetStr(JObject o, string k)
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return "";
            return v.Type == JTokenType.String ? v.Value<string>() : v.ToString();
        }

        static bool GetBool(JObject o, string k)
        {
            var v = o?[k];
            return v != null && v.Type == JTokenType.Boolean && v.Value<bool>();
        }

        static double GetDouble(JObject o, string k)
        {
            var v = o?[k];
            if (v == null || v.Type == JTokenType.Null) return 0.0;
            if (v.Type == JTokenType.Integer) return v.Value<long>();
            if (v.Type == JTokenType.Float) return v.Value<double>();
            return 0.0;
        }
    }
}
