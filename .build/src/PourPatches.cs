using System;
using HarmonyLib;

namespace SmarterTrading
{
    /// <summary>
    /// 放行「玩家水容器 → 顾客未拥有水容器」这一种目标判定。
    /// 原版 <c>MayTarget/CanTarget</c> 开头就把「目标未拥有」判死，这里只在极窄的条件下
    /// 把 false 翻成 true；已经可用的组合一律不动。
    /// </summary>
    [HarmonyPatch(typeof(GameItem), nameof(GameItem.MayTarget))]
    internal static class PourTargetMayPatch
    {
        private static void Postfix(GameItem __instance, GameItem targetItem, ref bool __result)
        {
            if (__result)
            {
                return;
            }
            try
            {
                if (WaterPour.CanPourTo(__instance, targetItem))
                {
                    __result = true;
                }
            }
            catch (Exception e)
            {
                ModEntry.Warn("MayTarget 放行失败: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(GameItem), nameof(GameItem.CanTarget))]
    internal static class PourTargetCanPatch
    {
        private static void Postfix(GameItem __instance, GameItem targetItem, ref bool __result)
        {
            if (__result)
            {
                return;
            }
            try
            {
                if (WaterPour.CanPourTo(__instance, targetItem))
                {
                    __result = true;
                }
            }
            catch (Exception e)
            {
                ModEntry.Warn("CanTarget 放行失败: " + e.Message);
            }
        }
    }

    /// <summary>
    /// 倒水完成后按「水」的价值差额结算。
    ///
    /// 用 Prefix 先记下目标容器此刻的水价值与容积，Postfix 再取差值——这样天然处理了
    /// 「只灌了一部分」「瓶里原本还有一点水」等情况，也不需要自己复刻原版的液体拆分逻辑。
    /// </summary>
    [HarmonyPatch(typeof(WaterHelper), nameof(WaterHelper.TransferLiquid))]
    internal static class PourChargePatch
    {
        private static void Prefix(GameItem sourceContainer, GameItem targetContainer, out PourState __state)
        {
            __state = null;
            try
            {
                PourContext context = WaterPour.ResolveContext(sourceContainer, targetContainer, requireTransfer: true);
                if (context == null)
                {
                    return; // 不是本模组管的场景，原版照常倒，不结算
                }
                __state = new PourState
                {
                    Context = context,
                    UnitValueBefore = targetContainer.unitValue,
                    VolumeBefore = WaterPour.VolumeUl(targetContainer)
                };
            }
            catch (Exception e)
            {
                __state = null;
                ModEntry.Warn("灌装前记录失败: " + e.Message);
            }
        }

        private static void Postfix(GameItem targetContainer, PourState __state)
        {
            if (__state == null)
            {
                return;
            }
            try
            {
                WaterPour.Settle(__state, targetContainer);
            }
            catch (Exception e)
            {
                ModEntry.Err("灌装结算失败: " + e);
            }
        }
    }

    /// <summary>
    /// 买回「灌过水的容器」时，把顾客为这口水付掉的预算补足。
    ///
    /// 挂在 <c>StoreClient.OnItemSold</c>（原版买回时唯一退预算的地方，
    /// <c>PlayerStore.OnItemBought</c> 里调用，且只此一处调用点）：
    /// 原版按 <c>budgetConversionRate</c>（默认 50%）退，本模组把差额补到「当初实收的水钱」。
    /// 只在容器带 <see cref="WaterPour.PaidWaterTag"/> 时生效，原版普通买卖不受影响。
    /// </summary>
    [HarmonyPatch(typeof(StoreClient), nameof(StoreClient.OnItemSold))]
    internal static class PouredWaterRefundPatch
    {
        private static void Postfix(StoreClient __instance, GameItem item, int soldAmount)
        {
            try
            {
                WaterPour.TopUpBudgetAfterBuyBack(__instance, item, soldAmount);
            }
            catch (Exception e)
            {
                ModEntry.Warn("买回灌装容器补足预算失败: " + e.Message);
            }
        }
    }

    /// <summary>
    /// 拖拽时鼠标旁那行提示（「对桶装水使用储水罐」）换成「提示 + 价格明细」。
    ///
    /// 为什么不能只往物品 tooltip 里加行：拖拽状态下 <c>ItemMouseDragHandler</c> 用的是
    /// <c>OverlayHandler.ShowTooltip</c>，显示的是这行动作提示，物品 tooltip 根本不会出现，
    /// 所以玩家看到的是「对桶装水使用储水罐」把整块区域占了。
    ///
    /// 这里只在「确实是本模组的灌装场景」时接管：按原版同样的一句话重建提示，再补上明细。
    /// 其余情况一律放行原版。
    /// </summary>
    [HarmonyPatch(typeof(RichTextGenerator), nameof(RichTextGenerator.UseItemOnOtherItem))]
    internal static class PourDragLabelPatch
    {
        private static bool Prefix(GameItem sourceItem, GameItem targetItem, bool canInsert, RichTextBuilder customBuilder, ref RichText __result)
        {
            if (!canInsert || customBuilder == null)
            {
                return true;
            }
            try
            {
                if (WaterPour.ResolveContext(sourceItem, targetItem, requireTransfer: true) == null)
                {
                    return true;
                }

                string sourceName = sourceItem.GetDisplayName(preferCustomName: true);
                string targetName = "<u>" + targetItem.GetDisplayName(preferCustomName: true) + "</u>";
                customBuilder.Clear();
                // 末位是 end：RichTextBuilder 的换行是「下一次 Add 开头的 nextBegin」补出来的，
                // 这里必须显式给 "\n"，否则价格明细会跟这行提示挤在同一行。
                customBuilder.Add(
                    LocHelper.GetLocalizedMechanic("mech_drag_use_on", sourceName, targetName)
                        + " " + ModEntry.T("ui_and_sell", "并卖出"),
                    RenderHandler.ColorPalette.LightGreen,
                    "\n");
                SalePricing.AppendPourBreakdown(customBuilder, targetItem, sourceItem);
                __result = customBuilder.Build();
                return false;
            }
            catch (Exception e)
            {
                ModEntry.Warn("灌装提示改写失败: " + e.Message);
                return true;
            }
        }
    }
}
