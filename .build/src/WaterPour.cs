using System;
using UnityEngine;

namespace SmarterTrading
{
    /// <summary>一次灌装结算所需的上下文（顾客 + 存档对象）。</summary>
    internal sealed class PourContext
    {
        internal StoreClient Client;
        internal PlayerStore Store;
    }

    /// <summary>倒水前后的快照，由 <see cref="PourChargePatch"/> 在 Prefix/Postfix 之间传递。</summary>
    internal sealed class PourState
    {
        internal PourContext Context;

        /// <summary>倒之前：顾客容器里水对应的 unitValue（1 升水 = 40）。</summary>
        internal long UnitValueBefore;

        /// <summary>倒之前：顾客容器里的水量（微升）。</summary>
        internal int VolumeBefore;
    }

    /// <summary>
    /// 特性 2：把水灌进顾客自带的（未拥有的）容器，只按水计价。
    ///
    /// 原版拦在哪：
    ///   · <c>GameItem.MayTarget / CanTarget</c> 开头一句
    ///     「target 未拥有且 source 没有 CAN_USE_ON_NON_OWNED_TAG → return false」，
    ///     顾客摆在柜台上的货全都过不去；
    ///   · 水容器自带的 <c>mayThisTargetItemFunc</c> 里还有一道
    ///     <c>WaterHelper.CanUseWaterContainer(t)</c>，而它只认「已拥有」。
    ///   · 但真正执行倒水的 <c>onThisTargetItemFunc</c> 只判
    ///     「t 是不是 LIQUID_CONTAINER_TAG」就调 <c>WaterHelper.TransferLiquid</c>，
    ///     不再查所有权——所以只要放行目标判定，倒水动作本身是原版的完整流程
    ///     （音量/贴图/水质特征/价值全部由原版更新）。
    ///
    /// 本模组只做两件事：
    ///   · 在「顾客想买这口水 + 目标是他摆在柜台上的空容器」时放行目标判定；
    ///   · 倒完之后按目标容器里「水」的价值差额结算（容器本体不计价，容器归顾客）。
    /// 灌多少完全交给原版：min(目标剩余容积, 水源剩余水量)，灌装量不受本模组影响。
    /// </summary>
    internal static class WaterPour
    {
        internal const string ContainerTag = "LIQUID_CONTAINER_TAG";
        internal const string WineContainerTag = "WINE_CONTAINER_TAG";

        /// <summary>
        /// 记在顾客容器上的标记：「这只容器里的水是本模组卖出去的」，值记着当时实收的水钱（仅用于日志）。
        /// 买回时靠它把预算按 <c>budgetConversionRate</c> 补到 100%（见
        /// <see cref="TopUpBudgetAfterBuyBack"/>）。
        ///
        /// 「已拥有的物品」才是 <c>ItemFeature</c> 的合法载体，所以这里用 tag 而不是复用
        /// 零售加价特性：<c>RetailMarkUp</c> 是原版给**每件卖过的货**都挂的市场特性，
        /// 拿它当标记会把补款扩散到原版的所有买卖（卖掉再买回就能把预算刷回来，
        /// 而原版买回价可能低于卖出价，等于无限刷钱）。本模组只为自己的灌水负责。
        /// </summary>
        internal const string PaidWaterTag = "SMARTER_PAID_WATER_TAG";

        /// <summary>
        /// 目标判定放行：只接受「玩家自己的水容器 → 顾客摆在柜台上的未拥有水容器」。
        /// 这个函数会被拖拽/悬停每帧调用，前面几行全是廉价判定，重判定放最后。
        /// </summary>
        internal static bool CanPourTo(GameItem source, GameItem target)
        {
            return ResolveContext(source, target, requireTransfer: true) != null;
        }

        internal static PourContext ResolveContext(GameItem source, GameItem target, bool requireTransfer)
        {
            if (!ModEntry.PourEnabled)
            {
                return null;
            }
            if (source == null || target == null || ReferenceEquals(source, target))
            {
                return null;
            }
            if (!target.IsTag(ContainerTag) || target.IsTag(WineContainerTag))
            {
                return null;
            }
            if (GeneralHelper.IsItemOwned(target))
            {
                return null; // 只处理顾客自带（未拥有）的容器
            }
            if (!source.IsTag(ContainerTag) || source.IsTag(WineContainerTag))
            {
                return null;
            }
            if (!GeneralHelper.IsItemOwned(source))
            {
                return null; // 只有玩家自己的水才能倒
            }
            if (!IsOnStoreCounter(target))
            {
                return null; // 只处理柜台上的货（顾客的货在「顾客侧」前柜台，也可能被拖到后柜台）
            }
            if (requireTransfer)
            {
                if (WaterHelper.IsEmpty(source) || WaterHelper.IsFull(target))
                {
                    return null; // 没水可倒 / 对方已满
                }
            }
            if (!PlayerStore.HasInstance)
            {
                return null;
            }

            PlayerStore store = PlayerStore.Instance;
            if (store.isClientBeingArrested || store.isHandlingShootout)
            {
                return null;
            }

            StoreClientInstance instance = store.currentClientInstance;
            if (instance == null)
            {
                return null;
            }
            StoreClient client = instance.GetClientBlueprint();
            if (client == null || client.isBusinessComplete)
            {
                return null;
            }
            if (client.useClientBudget && client.GetBudget() <= 0)
            {
                return null; // 一分钱没有，倒进去就是白送
            }
            // 顾客得真的想买这口水（水质/纯净度偏好都算在内）
            if (!client.IsClientBuyingThisItem(source, ignoreContents: true))
            {
                return null;
            }

            return new PourContext { Client = client, Store = store };
        }

        /// <summary>
        /// 是不是「顾客摆在柜台上、还没买下的水容器」。灌装目标判定与 tooltip 报价共用这一条，
        /// 保证提示只在真能倒的场合出现。
        /// </summary>
        internal static bool IsClientContainerOnCounter(GameItem item)
        {
            if (item == null || !item.IsTag(ContainerTag) || item.IsTag(WineContainerTag))
            {
                return false;
            }
            if (GeneralHelper.IsItemOwned(item))
            {
                return false;
            }
            return IsOnStoreCounter(item);
        }

        /// <summary>
        /// 物品是否摆在店里两块柜台上。顾客的货由 <c>AddDirectSellingItemToTable</c> 放进
        /// <c>frontInvinvElement</c>（顾客一侧），也可能被拖到 <c>backInvinvElement</c>（自己一侧），
        /// 两边都算，别的地方不算。
        /// </summary>
        private static bool IsOnStoreCounter(GameItem item)
        {
            EmporiumEntry entry = EmporiumEntry.Instance;
            if (entry == null)
            {
                return false;
            }
            return IsUnderInventory(item, entry.frontInvinvElement) || IsUnderInventory(item, entry.backInvinvElement);
        }

        private static bool IsUnderInventory(GameItem item, GameInventory inventory)
        {
            if (item == null || inventory == null)
            {
                return false;
            }
            GameItem current = item;
            for (int depth = 0; depth < 16 && current != null; depth++)
            {
                GameInventory parent = current.parentInventory;
                if (parent == null)
                {
                    return false;
                }
                if (ReferenceEquals(parent, inventory))
                {
                    return true;
                }
                current = parent.GetParentItem();
            }
            return false;
        }

        /// <summary>容器里的水量（微升）。</summary>
        internal static int VolumeUl(GameItem item)
        {
            try
            {
                return WaterHelper.GetCurrentCapacityUL(item);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 倒完之后结算：按「这只瓶子里的水，正常卖给你眼前这个客人值多少钱」收费。
        ///
        /// 两件事必须一起做，缺一就会出套利：
        ///   1. <c>SyncMarketFeatures</c> 补上缺水之类的市场事件特性（空瓶是 HOUSEHOLD_GOOD，原版不给它挂）；
        ///   2. <c>SalePricing.EnsurePourFeatures</c> 把零售加价/小费这些「玩家卖货」特性**永久**挂上。
        ///      特性一旦挂上，原版不会再摘；瓶子自己的估价随之变成「水价 + 含加价的容器」，
        ///      于是收费 264 + 容器 15 ≈ 买回 279 ≈ 再卖 279，倒手不再有利润。
        ///      如果只临时挂着量价、量完摘掉，玩家就能用裸价 228 把瓶子买回去再卖 279 ——
        ///      实测就是这么刷钱的。
        ///
        /// 收费本身取「瓶子议价在灌前/灌后两个水量下的差额」，与容器价无关，
        /// 天然处理「只灌了一部分」「瓶里原本有水」「水质混合」。
        /// </summary>
        internal static void Settle(PourState state, GameItem target)
        {
            if (state == null || state.Context == null || target == null)
            {
                return;
            }

            int pouredUl = VolumeUl(target) - state.VolumeBefore;
            if (pouredUl <= 0)
            {
                return;
            }

            SyncMarketFeatures(target);
            SalePricing.EnsurePourFeatures(target);

            long price = SalePricing.PouredWaterValue(target, state.UnitValueBefore);
            if (price <= 0L)
            {
                ModEntry.Log($"灌装 {pouredUl / 1000000f:0.###}L：水价没涨（unitValue {state.UnitValueBefore} → {target.unitValue}），不计价");
                return;
            }
            if (price > int.MaxValue)
            {
                price = int.MaxValue;
            }

            int billed = (int)price;
            StoreClient client = state.Context.Client;
            PlayerStore store = state.Context.Store;

            int paid = billed;
            if (client.useClientBudget)
            {
                int budget = client.GetBudget();
                if (paid > budget)
                {
                    paid = budget; // 与原版 SellItem 一致：不够就只收得到这么多
                }
            }

            if (paid > 0)
            {
                if (client.useClientBudget)
                {
                    client.ModBudget(-paid);
                    if (client.GetBudget() < 0)
                    {
                        client.OverrideBudget(0);
                    }
                    // 记下这笔预算，等玩家把这只容器买回去时退回（原版买回是不退预算的）
                    RecordPaidWater(target, paid);
                }
                store.playerCash += paid;

                try { client.OnItemBought(target, paid); } catch { }
                try { StoreReputation.OnItemTraded(client.clientFaction, billed, paid, client, isPlayerSold: true, target); } catch { }
                try { RunStats.OnItemSold(target, paid, 0, client); } catch { }
            }

            TrackBadWater(state.Context, target, pouredUl);

            try
            {
                NegociationUIManager negotiation = NegociationUIManager.Instance;
                if (negotiation != null)
                {
                    if (paid > 0 && negotiation.audioSourceCashRegister != null)
                    {
                        negotiation.audioSourceCashRegister.Play();
                    }
                    negotiation.RefreshUI();
                }
            }
            catch (Exception e)
            {
                ModEntry.Warn("灌装后刷新界面失败: " + e.Message);
            }

            string whole = "-";
            try { whole = target.GetNegociatedValue().ToString(); } catch { }
            ModEntry.Log($"灌装 {pouredUl / 1000000f:0.###}L：水价 {billed}，实收 {paid}，顾客余额 {client.GetBudget()}，整瓶议价 {whole}（含加价，= 买回价）");
            if (paid < billed)
            {
                ModEntry.Warn($"顾客钱不够：应结 {billed}，只收到 {paid}");
            }
        }

        /// <summary>
        /// 把这笔「顾客为水实际付掉的预算」累加记在容器上，多次续灌会累加。
        /// 只记预算真被扣掉的情况（<c>useClientBudget</c>），否则退款时无账可退。
        /// </summary>
        private static void RecordPaidWater(GameItem item, int paid)
        {
            if (item == null || paid <= 0)
            {
                return;
            }
            try
            {
                int total = paid;
                if (item.IsTag(PaidWaterTag))
                {
                    total += item.GetTagReadonly(PaidWaterTag).GetInt();
                }
                item.EnableTag(PaidWaterTag, skipValidate: true);
                item.ModifyTag(PaidWaterTag, state => state.SetInt(total), skipValidate: true);
            }
            catch (Exception e)
            {
                ModEntry.Warn("记录灌装预算失败: " + e.Message);
            }
        }

        /// <summary>
        /// 玩家把灌过水的容器买回来时，把顾客的预算**补足到买回价**（也就是玩家刚付的那笔钱）。
        ///
        /// 原版会退一部分：<c>PlayerStore.OnItemBought</c> → <c>StoreClient.OnItemSold</c> 里
        /// <c>clientBudget += cost * budgetConversionRate / 100</c>，默认只有 50%。
        /// 所以这里不能直接再退一整笔（会变成双重退款，实测 484 + 408 + 792 = 1684），
        /// 而是拿「买回价」减去「原版刚退给他的那份」，只补差额。
        ///
        /// 为什么补到**买回价**而不是「当初的水钱」：买回是整瓶交易，玩家连容器一起买走了，
        /// 顾客理应拿到整个瓶子的钱。只补水钱会凭空蒸发掉容器那一份——
        /// 实测 2L 瓶：灌水 264、买回 279（原版退 139 + 我补 125 = 264），
        /// 顾客净少 15，再 279 卖回去后余额停在 997 而不是 1012。
        /// 补到 279 后：预算回到「灌装前 + 容器价」，顾客再把整瓶买回去时正好净花 264（只花水钱），
        /// 一分不蒸发、也一分不凭空多出。
        ///
        /// 只处理带 <see cref="PaidWaterTag"/> 的容器，原版的普通买卖不受影响。
        /// </summary>
        internal static void TopUpBudgetAfterBuyBack(StoreClient client, GameItem item, int soldAmount)
        {
            if (item == null || !item.IsTag(PaidWaterTag))
            {
                return;
            }

            int paidWater = 0;
            try
            {
                paidWater = item.GetTagReadonly(PaidWaterTag).GetInt();
            }
            catch (Exception e)
            {
                ModEntry.Warn("读取灌装预算失败: " + e.Message);
            }
            try
            {
                // 先清标记再补钱：同一只瓶子不能被补第二次
                item.DisableTag(PaidWaterTag, skipValidate: true);
            }
            catch (Exception e)
            {
                ModEntry.Warn("清除灌装预算标记失败: " + e.Message);
            }

            if (client == null || !client.useClientBudget)
            {
                return;
            }

            int credited = soldAmount * client.budgetConversionRate / 100;
            int topUp = soldAmount - credited;
            if (topUp <= 0)
            {
                ModEntry.Log($"买回灌装容器：原版已退 {credited}（{client.budgetConversionRate}%），买回价 {soldAmount}，无需补足");
                return;
            }

            client.ModBudget(topUp);
            try
            {
                NegociationUIManager negotiation = NegociationUIManager.Instance;
                if (negotiation != null)
                {
                    negotiation.RefreshUI();
                }
            }
            catch (Exception e)
            {
                ModEntry.Warn("补足预算后刷新界面失败: " + e.Message);
            }

            ModEntry.Log($"买回灌装容器：原版退 {credited}（{client.budgetConversionRate}%），补足 {topUp}，买回价 {soldAmount}（其中水钱 {paidWater}）全额回到预算，顾客余额 {client.GetBudget()}");
        }

        /// <summary>
        /// 把「当前市场事件」的特性同步到目标容器上，和原版 <c>PlayerStore.RefreshCounterItem</c> 一致。
        ///
        /// 事件加成是按 <c>itemType</c> 挂的：缺水事件的 <c>WATER +35%</c> 只认 <c>WATER</c> 类型。
        /// 顾客那只瓶子进店时是空的（<c>HOUSEHOLD_GOOD</c>），倒完水才变成 <c>WATER</c>；
        /// 而原版只对「已拥有 + 在后柜台」的物品重挂特性（<c>UpdateContainerGoodType</c>），
        /// 所以此刻瓶子上还没有这 35%。不补这一下，差额就永远比「买回这只瓶子」便宜一截。
        /// </summary>
        private static void SyncMarketFeatures(GameItem item)
        {
            try
            {
                item.RemoveFeatureRelatedToEvent();
                item.AddFeatureRelatedToEvent();
            }
            catch (Exception e)
            {
                ModEntry.Warn("同步市场特性失败: " + e.Message);
            }
        }

        /// <summary>
        /// 补上原版「卖脏水/假标水」的记录。原版按容器当前容量算，这里按实际倒过去的体积算，
        /// 分几次灌也不会重复计数。
        /// </summary>
        private static void TrackBadWater(PourContext context, GameItem target, int pouredUl)
        {
            try
            {
                SecData secData = context.Store.secData;
                if (secData == null)
                {
                    return;
                }
                StoreClient client = context.Client;
                if (client != null && client.IsFaction("FACTION_TOURIST"))
                {
                    return;
                }
                ItemFeature waterFeature = WaterFeatureHelper.GetWaterFeature(target);
                if (waterFeature == null)
                {
                    return;
                }
                float weight = SecData.GetMislabeledWaterWeight(waterFeature, out bool isGhostwater);
                if (weight <= 0f)
                {
                    return;
                }
                int mislabeledMl = (int)Math.Round(pouredUl / 1000f * weight);
                if (mislabeledMl <= 0)
                {
                    return;
                }
                if (isGhostwater)
                {
                    secData.SellDirtyWater(mislabeledMl);
                }
                else
                {
                    secData.SellMislabeledWater(mislabeledMl);
                }
            }
            catch (Exception e)
            {
                ModEntry.Warn("脏水记账失败: " + e.Message);
            }
        }
    }
}
