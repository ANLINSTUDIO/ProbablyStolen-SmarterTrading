using System;
using UnityEngine;

namespace SmarterTrading
{
    /// <summary>
    /// 「更聪明的交易」模组入口。
    ///
    /// 两个特性，都挂在原版链路上，不新增界面：
    ///   1. 预算收尾台词：<see cref="BudgetLinePatch"/> 挂在
    ///      <c>StoreClientDialogList.CreateAfterSellingToClientDialog</c> 上，把原版写死的
    ///      「预算 &lt; 10」阈值换成「本次实际成交均价」。
    ///   2. 灌装给顾客：<see cref="PourTargetPatch"/> + <see cref="PourChargePatch"/>。
    ///      原版里顾客摆到柜台上的货是「未拥有」的，<c>GameItem.MayTarget/CanTarget</c>
    ///      的开头就把「目标未拥有」直接判死，<c>WaterHelper.CanUseWaterContainer</c> 也只认
    ///      已拥有的容器——所以顾客的空瓶子根本倒不进水。这里放行这一种情况，水量仍由原版
    ///      <c>WaterHelper.TransferLiquid</c> 决定（不超过目标剩余容积、不超过水源剩余），
    ///      本模组只负责事后按「正常卖价」结算，并在顾客容器的 tooltip 上提前显示灌装售价。
    ///
    /// 注意：ModLoader 加载 DLL 时已对整个程序集 PatchAll，本入口不再自行调用 Harmony。
    /// </summary>
    public sealed class ModEntry : IMod
    {
        public const string MOD_ID = "smarter_trading";

        private static ModLog _log;

        internal static ModConfig Cfg;
        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<bool> CfgBudgetLine;
        internal static ConfigEntry<bool> CfgPour;
        internal static ConfigEntry<bool> CfgVerbose;

        public void Init(ModManifest manifest)
        {
            try { _log = new ModLog(manifest); } catch { _log = null; }

            try
            {
                string modId = (manifest != null && !string.IsNullOrEmpty(manifest.ID)) ? manifest.ID : MOD_ID;
                Cfg = ModConfig.For(modId);

                CfgEnabled = Cfg.Define("Enabled", true, "General",
                    () => T("cfg_enabled", "启用「更聪明的交易」"),
                    () => T("cfg_enabled_tip", "总开关。关闭后两个特性都不生效，补丁保持沉默。"));

                CfgBudgetLine = Cfg.Define("SmartBudgetLine", true, "General",
                    () => T("cfg_budget_line", "预算不足时不再追问「就这些吗」"),
                    () => T("cfg_budget_line_tip", "顾客剩下的钱低于本次实际成交均价时，改用原版「成交。/ 我要的就是这个。/ 下次见。」这组收尾台词，而不是继续问「就这些吗？」。均价取本次来访里你卖给他的所有物品的标准成交价平均值。"));

                CfgPour = Cfg.Define("PourToClientContainer", true, "General",
                    () => T("cfg_pour", "允许把水灌进顾客自带的容器"),
                    () => T("cfg_pour_tip", "顾客想买水时，可以直接把自己的水倒进柜台上他们那只「未拥有」的空瓶/水壶里。灌多少仍按原版规则（不超过对方剩余容积、不超过水源剩余水量），只按水计价，容器始终归顾客。"));

                CfgVerbose = Cfg.Define("Verbose", false, "Diagnostics",
                    () => T("cfg_verbose", "详细日志"),
                    () => T("cfg_verbose_tip", "打印灌装与台词替换的判定细节，排查问题时开启。"));

                Cfg.Save();
            }
            catch (Exception e)
            {
                Warn("配置初始化失败: " + e.Message);
            }

            Log("更聪明的交易 已加载（Harmony 补丁由 ModLoader 自动应用）");
            HookTooltip();
        }

        public void OnEnable()
        {
            HookTooltip();
        }

        public void OnDisable()
        {
            SaleHistory.Reset();
            if (_tooltipHooked)
            {
                try { ModHook.OnCreateTooltipLate -= OnTooltipLate; } catch { }
                _tooltipHooked = false;
            }
        }

        private static bool _tooltipHooked;

        /// <summary>
        /// 用原版给模组留的 tooltip 钩子加一行「灌装售价」，每次调用都是幂等的。
        /// <c>ModHook.SafeInvoke</c> 会兜住异常，这里再包一层只是防止自己炸到别人。
        /// </summary>
        private static void HookTooltip()
        {
            if (_tooltipHooked)
            {
                return;
            }
            try
            {
                ModHook.OnCreateTooltipLate += OnTooltipLate;
                _tooltipHooked = true;
            }
            catch (Exception e)
            {
                Warn("挂载 tooltip 钩子失败: " + e.Message);
            }
        }

        private static void OnTooltipLate(RichTextBuilder builder, GameItem item)
        {
            try
            {
                SalePricing.AppendPourPriceTooltip(builder, item);
            }
            catch (Exception e)
            {
                Warn("灌装售价提示失败: " + e.Message);
            }
        }

        internal static bool Enabled => CfgEnabled == null || CfgEnabled.Value;
        internal static bool BudgetLineEnabled => Enabled && (CfgBudgetLine == null || CfgBudgetLine.Value);
        internal static bool PourEnabled => Enabled && (CfgPour == null || CfgPour.Value);
        internal static bool Verbose => CfgVerbose != null && CfgVerbose.Value;

        /// <summary>外置文本：Localization/*.csv 优先，取不到时退回代码里的中文兜底。</summary>
        internal static string T(string key, string fallback)
        {
            try
            {
                string s = ModHelper.GetLocalized(MOD_ID, key);
                if (!string.IsNullOrEmpty(s) && s != key)
                {
                    return s;
                }
            }
            catch
            {
                // 本地化表还没加载时直接走兜底
            }
            return fallback;
        }

        internal static void Log(string s) { try { _log?.Log("[更聪明的交易] " + s); } catch { } }
        internal static void Warn(string s) { try { _log?.Warning("[更聪明的交易] " + s); } catch { } }
        internal static void Err(string s) { try { _log?.Error("[更聪明的交易] " + s); } catch { } }
    }
}
