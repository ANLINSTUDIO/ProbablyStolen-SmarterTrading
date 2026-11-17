using System;
using HarmonyLib;

namespace SmarterTrading
{
    /// <summary>
    /// 特性 1：预算不足时不再追问「就这些吗」。
    ///
    /// 原版 <c>StoreClientDialogList.CreateAfterSellingToClientDialog</c> 只用一个写死的
    /// 阈值判断顾客是不是「没钱了」：
    ///
    ///     if (useClientBudget &amp;&amp; GetBudget() &lt; 10)  → dialog_util_after_sell_done（「就这些吗？」）
    ///     else                                        → dialog_util_after_sell（「还有别的吗？」）
    ///
    /// 但 10 信用点是凭空定的：顾客兜里剩 200、而你柜台上一件货卖 800，他同样买不起，
    /// 却还会被原版当成「还想再买」。这里把判断换成本次来访的实际成交均价——钱不够再买
    /// 一件同价位的货时（包括原版那句「余额 &lt; 10」的分支），一律改用原版那组
    /// 「成交。/ 我要的就是这个。/ 下次见。」收尾台词，不再追问。
    ///
    /// 没有本次成交记录（均价 0）时完全放行原版，行为不变。
    /// </summary>
    [HarmonyPatch(typeof(StoreClientDialogList), "CreateAfterSellingToClientDialog")]
    internal static class BudgetLinePatch
    {
        private static bool Prefix(string clientName, ref Dialogue __result)
        {
            try
            {
                if (!ModEntry.BudgetLineEnabled || !PlayerStore.HasInstance)
                {
                    return true;
                }

                StoreClientInstance instance = PlayerStore.Instance.currentClientInstance;
                if (instance == null)
                {
                    return true;
                }

                StoreClient client = instance.GetClientBlueprint();
                if (client == null || !client.useClientBudget)
                {
                    return true;
                }

                int budget = client.GetBudget();

                double average = SaleHistory.Average(client);
                if (average <= 0.0)
                {
                    return true; // 本次来访还没成交过，交给原版
                }
                if (budget >= average)
                {
                    return true; // 还买得起下一件，正常追问
                }

                // 钱不够再买一件（含原版写死的「余额 &lt; 10」那一段）→ 换成收尾台词，
                // 不再问「就这些吗？」/「还有别的吗？」
                Dialogue dialogue = new Dialogue().SetText(clientName, StoreClientDialogList.GetRandomDialogDoneString());
                dialogue.priority = 4;
                __result = dialogue;

                ModEntry.Log($"预算不足：余额 {budget} < 成交均价 {average:0.#}，改用收尾台词代替追问");
                return false;
            }
            catch (Exception e)
            {
                ModEntry.Warn("预算收尾台词判定失败，回退原版: " + e.Message);
                return true;
            }
        }
    }
}
