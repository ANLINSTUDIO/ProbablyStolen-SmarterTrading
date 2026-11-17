using System;
using System.Collections.Generic;
using UnityEngine;

namespace SmarterTrading
{
    /// <summary>
    /// 灌水成交价：按「把自己的货卖给眼前这个客人」的价签算，而不是按瓶子本身的值。
    ///
    /// 原版把零售加价、小费、香水、上瘾惩罚这些加成都做成 <see cref="ItemFeature"/>
    /// （<c>ValueStage.Market</c>），在 <c>GameItem.OnAddedToWeightedArea</c> 里按
    /// 「已拥有 + 玩家在卖货」的条件挂到物品上。顾客那只瓶子是未拥有的，这些特性永远挂不上，
    /// 所以它的 <c>GetNegociatedValue</c> 天生比正常售价少一截。
    ///
    /// 关键：这些特性一旦挂上，原版**从不移除**（<c>AddItemFeature</c> 按 identifier 去重，
    /// 之后换个客人重新上架也不会重复累加）。也就是说同一件货被玩家卖过一次之后，
    /// 它的估价就已经含上玩家的加价了。灌装结算必须和这个行为对齐：
    ///
    ///   · 只「临时」挂上量价、量完摘掉 → 瓶子估价仍是裸价，玩家就能用裸价把它买回来再高价卖出，
    ///     形成「灌水 → 买回 → 卖回」的套利（实测 2L：收 264，买回 228，卖回 279）；
    ///   · 所以这里改成**永久**挂上，让瓶子自己的估价就等于「水价 + 容器价（含加价）」。
    ///     于是灌装收费 + 容器份额 = 买回价 = 再卖出去的价格，套利空间为零。
    ///
    /// 报价（拖拽提示 / 悬停 tooltip）则走 <see cref="EnsureCache"/>：临时挂上同一套特性，
    /// 按原版 <c>GetNegociatedValue</c> 的分档公式自己累加一遍，得出每升卖价与明细。
    /// </summary>
    internal static class SalePricing
    {
        /// <summary>1 升水的 unitValue 增量：<c>WaterFeatureHelper.GetWaterPrice</c> 里写死的 40/L。</summary>
        internal const double UnitValuePerLiter = 40.0;

        /// <summary>拖拽提示每帧都重建，报价与特性清单缓存半秒。</summary>
        private const float CacheSeconds = 0.5f;

        private static GameItem _cacheItem;
        private static GameItem _cacheSource;
        private static float _cacheAt = float.NegativeInfinity;
        private static double _cachePerLiter;
        private static readonly List<string> _innateLines = new List<string>(4);
        private static readonly List<string> _marketLines = new List<string>(4);

        /// <summary>
        /// 结算前把「正常卖货」那套市场加成永久挂到顾客的容器上。
        ///
        /// 挂上之后这只瓶子的 <c>GetNegociatedValue</c> 就等于「水 + 容器」的正常卖价，
        /// 玩家再从顾客手里买回来时也是这个价，倒手没有利润，套利就断了。
        /// 行为上与原版一致：<c>AddItemFeature</c> 自带 identifier 去重，重复灌装不会叠加。
        /// </summary>
        internal static void EnsurePourFeatures(GameItem bottle)
        {
            if (bottle == null)
            {
                return;
            }
            try
            {
                AttachMarketEventFeatures(bottle); // 空瓶是 HOUSEHOLD_GOOD，原版不会给它挂水事件加成
            }
            catch (Exception e)
            {
                ModEntry.Warn("补挂水事件特性失败: " + e.Message);
            }
            try
            {
                AttachSellingFeatures(bottle, null); // added = null：永久保留，不回收
            }
            catch (Exception e)
            {
                ModEntry.Warn("补挂卖货特性失败: " + e.Message);
            }
        }

        /// <summary>
        /// 这次灌进去的水值多少钱：同一只瓶子在「灌之前的水量 / 现在的水量」两个状态下的议价差额。
        ///
        /// 直接用差额而不是「单价 × 水量」，是因为差额天然与容器价、杂项价无关（两端都有、正好抵消），
        /// 也不会被四舍五入放大；而且它保证：
        ///     收费 + 容器份额 = 瓶子此刻的完整议价 = 玩家买回它的价格。
        /// </summary>
        internal static long PouredWaterValue(GameItem bottle, long unitValueBefore)
        {
            if (bottle == null)
            {
                return 0L;
            }
            long after;
            try
            {
                after = bottle.GetNegociatedValue();
            }
            catch (Exception e)
            {
                ModEntry.Warn("读取灌装后议价失败: " + e.Message);
                return 0L;
            }
            long before = ValueAtUnitValue(bottle, unitValueBefore);
            long price = after - before;
            return price > 0L ? price : 0L;
        }

        /// <summary>每升卖价（含品相、缺水等市场事件、零售加价、小费）。</summary>
        internal static double PricePerLiter(GameItem bottle, GameItem source)
        {
            EnsureCache(bottle, source);
            return _cachePerLiter;
        }

        /// <summary>「价格 X/L｜本次灌 YL ≈ Z 信用点」。取不到价（容器满了/不是水容器/没客人）时返回 null。</summary>
        internal static string BuildPourLine(GameItem bottle)
        {
            int amount;
            return BuildPourLine(bottle, null, out amount);
        }

        internal static string BuildPourLine(GameItem bottle, GameItem source)
        {
            int amount;
            return BuildPourLine(bottle, source, out amount);
        }

        private static string BuildPourLine(GameItem bottle, GameItem source, out int amount)
        {
            amount = 0;
            if (bottle == null)
            {
                return null;
            }
            double perLiter = PricePerLiter(bottle, source);
            if (perLiter <= 0.0)
            {
                return null;
            }

            amount = WaterHelper.GetFreeCapacity(bottle);
            bool limitedBySource = false;
            if (source != null)
            {
                int inSource = WaterPour.VolumeUl(source);
                if (inSource > 0 && inSource < amount)
                {
                    amount = inSource;
                    limitedBySource = true;
                }
            }
            if (amount <= 0)
            {
                return string.Format(ModEntry.T("ui_pour_price_full", "价格 {0:0.#}/L｜容器已满"), perLiter);
            }

            string total = TabletUI.Credits(TotalFor(perLiter, amount));
            return limitedBySource
                ? string.Format(ModEntry.T("ui_pour_now", "价格 {0:0.#}/L｜本次灌 {1:0.##}L ≈ {2}"), perLiter, amount / 1000000f, total)
                : string.Format(ModEntry.T("ui_pour_price", "价格 {0:0.#}/L｜还能灌 {1:0.##}L ≈ {2}"), perLiter, amount / 1000000f, total);
        }

        /// <summary>
        /// 拖拽提示的明细，照原版议价平板展开行的口径分条列出：
        /// 价格 → 品相（Innate）→ 市场与商人（Market）→ 顾客预算 → 预计卖出。
        ///
        /// 列出来的特性必须和结算时用的那一套完全一致（事件特性 + 玩家卖货特性），
        /// 否则明细里的加价和实际收费对不上。这里临时挂上、列完摘掉；
        /// 结算那次是永久挂上，两者用的是同一个 <see cref="AttachSellingFeatures"/>。
        /// </summary>
        internal static void AppendPourBreakdown(RichTextBuilder builder, GameItem bottle, GameItem source)
        {
            if (builder == null || bottle == null)
            {
                return;
            }

            int amount;
            string priceLine = BuildPourLine(bottle, source, out amount);
            if (string.IsNullOrEmpty(priceLine))
            {
                return;
            }
            builder.AddLine(priceLine, ignoreEmpty: true, RenderHandler.ColorPalette.LightTurquoise);

            EnsureCache(bottle, source);
            AppendSection(builder, _innateLines, LocUI("ui_nego_condition", "Condition"));
            AppendSection(builder, _marketLines, LocUI("ui_nego_market", "Market & merchants"));

            long total = TotalFor(_cachePerLiter, amount);

            PlayerStore store = PlayerStore.Instance;
            StoreClient client = (store != null && store.currentClientInstance != null)
                ? store.currentClientInstance.GetClientBlueprint()
                : null;

            if (client != null && client.useClientBudget)
            {
                int budget = client.GetBudget();
                bool overBudget = budget < total;
                builder.AddLine(
                    LocUI("ui_nego_client_budget", "Customer budget") + "  " + TabletUI.Credits(budget),
                    ignoreEmpty: true,
                    overBudget ? RenderHandler.ColorPalette.LightRed : RenderHandler.ColorPalette.VeryLightGray);
            }
            builder.AddLine(
                ModEntry.T("ui_pour_expected", "预计卖出") + "  " + TabletUI.Credits(total),
                ignoreEmpty: true,
                RenderHandler.ColorPalette.LightGreen);
        }

        private static long TotalFor(double perLiter, int microLiters)
        {
            if (perLiter <= 0.0 || microLiters <= 0)
            {
                return 0L;
            }
            return (long)Math.Round(perLiter * microLiters / 1000000.0);
        }

        /// <summary>
        /// 报价缓存：临时挂上与结算完全相同的特性，按原版 <c>GetNegociatedValue</c> 的分档公式
        /// （Innate 与 Market 相加、Final 连乘、容器价不参与品相）累加出「每升卖价」，
        /// 同时收下两段特性清单给明细用。
        ///
        /// 空瓶的水质特性是 <c>isDisabled</c> 的（<c>WaterFeatureHelper.InitWaterFeature</c> 里
        /// 按 <c>IsEmpty</c> 关掉），原版算法会跳过它；但灌满之后它必然是开着的，
        /// 所以要拿「灌进去的那份水」的品相补上——否则预览会少算整整一倍（纯水 +100%）。
        /// </summary>
        private static void EnsureCache(GameItem bottle, GameItem source)
        {
            if (bottle == null)
            {
                _cachePerLiter = 0.0;
                return;
            }
            float now = Time.unscaledTime;
            if (ReferenceEquals(_cacheItem, bottle) && ReferenceEquals(_cacheSource, source)
                && now - _cacheAt < CacheSeconds)
            {
                return;
            }
            _cacheItem = bottle;
            _cacheSource = source;
            _cacheAt = now;
            _cachePerLiter = 0.0;
            _innateLines.Clear();
            _marketLines.Clear();

            List<ItemFeature> temp = null;
            try
            {
                temp = AttachMarketEventFeatures(bottle);
                if (temp == null)
                {
                    temp = new List<ItemFeature>(4);
                }
                AttachSellingFeatures(bottle, temp);

                long innate = 0L;
                long market = 0L;
                double finalFactor = 1.0;
                CollectFeatures(bottle, ref innate, ref market, ref finalFactor);

                if (WaterPour.VolumeUl(bottle) <= 0)
                {
                    // 空瓶：用将要灌进去的水的品相补上（拿不到就退回瓶子自带的默认水质）
                    ItemFeature water = FindWaterFeature(source) ?? FindWaterFeature(bottle);
                    if (water != null)
                    {
                        innate += water.GetCurrentValueMod();
                        if (_innateLines.Count == 0 || !_innateLines.Contains(ToDisplay(water)))
                        {
                            _innateLines.Add(ToDisplay(water));
                        }
                    }
                }

                _cachePerLiter = UnitValuePerLiter * (1.0 + innate / 100.0) * (1.0 + market / 100.0) * finalFactor;
            }
            catch (Exception e)
            {
                ModEntry.Warn("灌装报价取样失败: " + e.Message);
            }
            finally
            {
                ReleaseSellingFeatures(bottle, temp);
            }
        }

        /// <summary>照原版 <c>GameItem.AccumulateFeatureStages</c> 的规则累加三个档位，顺手收下可显示的条目。</summary>
        private static void CollectFeatures(GameItem item, ref long innate, ref long market, ref double finalFactor)
        {
            for (int i = 0; i < item.itemFeatures.Count; i++)
            {
                ItemFeature feature = item.itemFeatures[i];
                if (feature == null || feature.isDisabled)
                {
                    continue;
                }
                switch (feature.GetValueStage())
                {
                    case ItemFeature.ValueStage.Market:
                        market += feature.GetCurrentValueMod();
                        AddLine(_marketLines, feature);
                        break;
                    case ItemFeature.ValueStage.Final:
                        finalFactor *= Math.Max(0.0, 1.0 + feature.GetCurrentValueMod() / 100.0);
                        break;
                    default:
                        innate += feature.GetCurrentValueMod();
                        AddLine(_innateLines, feature);
                        break;
                }
            }
        }

        private static void AddLine(List<string> into, ItemFeature feature)
        {
            string line = ToDisplay(feature);
            if (!string.IsNullOrEmpty(line) && !into.Contains(line))
            {
                into.Add(line);
            }
        }

        /// <summary>和原版展开行一样：看得见就用暴露版，看不见就用公开版（隐藏真实数值）。</summary>
        private static string ToDisplay(ItemFeature feature)
        {
            if (feature == null || !feature.IsInitiallyShown() || (feature.GetPublicHidden() && !feature.IsExposed()))
            {
                return null;
            }
            return feature.IsExposed() ? feature.GetExposedString() : feature.GetPublicDisplay();
        }

        private static ItemFeature FindWaterFeature(GameItem item)
        {
            if (item == null || !item.IsTag(WaterPour.ContainerTag))
            {
                return null;
            }
            try
            {
                return item.FindItemFeatureByCategory("CATEGORY_WATER_PURITY");
            }
            catch
            {
                return null;
            }
        }

        private static void AppendSection(RichTextBuilder builder, List<string> lines, string title)
        {
            if (lines == null || lines.Count == 0)
            {
                return;
            }
            builder.AddLine(title, ignoreEmpty: true, RenderHandler.ColorPalette.LightYellow);
            foreach (string line in lines)
            {
                builder.AddLine(line, ignoreEmpty: true, RenderHandler.ColorPalette.None);
            }
        }

        private static string LocUI(string key, string fallback)
        {
            try
            {
                string s = LocHelper.GetLocalizedUIOrDefault(key, fallback);
                if (!string.IsNullOrEmpty(s))
                {
                    return s;
                }
            }
            catch
            {
                // 本地化表没就绪时走兜底
            }
            return fallback;
        }

        /// <summary>临时把水量对应的 unitValue 设成指定值量一次价，量完还原。</summary>
        private static long ValueAtUnitValue(GameItem item, long unitValue)
        {
            long original = item.unitValue;
            try
            {
                item.unitValue = unitValue;
                return item.GetNegociatedValue();
            }
            finally
            {
                item.unitValue = original;
            }
        }

        /// <summary>
        /// 补上「水」的市场事件加成（缺水 +35% 之类）。
        ///
        /// 事件特性是按 <c>itemType</c> 挂的，只认 <c>WATER</c>。顾客的空瓶还没装水，
        /// 类型还是 <c>HOUSEHOLD_GOOD</c>，身上没有任何水的加成——但报价要按「灌满之后」算，
        /// 所以先挂上再算。已经有这些特性的物品（结算时的瓶子）自动跳过。
        /// </summary>
        private static List<ItemFeature> AttachMarketEventFeatures(GameItem item)
        {
            List<ItemFeature> added = null;
            StoreStation station = StoreStation.Instance;
            if (station == null || station.storeEventManager == null)
            {
                return null;
            }
            List<StoreEvent> events = station.storeEventManager.GetActiveEvents();
            if (events == null)
            {
                return null;
            }
            foreach (StoreEvent storeEvent in events)
            {
                if (storeEvent == null || storeEvent.negociationDatas == null)
                {
                    continue;
                }
                foreach (NegociationData data in storeEvent.negociationDatas)
                {
                    if (data == null || data.itemType != "WATER" || item.IsAlreadyContainFeatureWithId(data.identifier))
                    {
                        continue;
                    }
                    ItemFeature feature = ItemFeature.CreateFeatureFromNegociationData(data);
                    if (feature == null)
                    {
                        continue;
                    }
                    item.AddItemFeature(feature);
                    if (added == null)
                    {
                        added = new List<ItemFeature>(4);
                    }
                    added.Add(feature);
                }
            }
            return added;
        }

        /// <summary>
        /// 照 <c>GameItem.OnAddedToWeightedArea</c> 里「已拥有 + 玩家卖货」那一段，把市场加成挂上。
        /// 顺序、条件必须和原版一致，否则算出来的价签和正常卖货对不上。
        /// </summary>
        private static void AttachSellingFeatures(GameItem item, List<ItemFeature> added)
        {
            PlayerStore store = PlayerStore.Instance;
            if (item == null || store == null || store.currentClientInstance == null)
            {
                return;
            }
            StoreClient client = store.currentClientInstance.GetClientBlueprint();
            if (client == null)
            {
                return;
            }

            if (store.IsCurrentClientAcceptMarkup())
            {
                TryAdd(item, added, ItemFeatureList.RetailMarkUp());
            }
            if (client.perfumeInfluence == 1)
            {
                TryAdd(item, added, ItemFeatureList.NeuroActivePerfumeSelling());
            }
            else if (client.perfumeInfluence == 2)
            {
                TryAdd(item, added, ItemFeatureList.PheromonePerfumeSelling());
            }
            if (HealthData.HasAnyAddiction() && store.healthData.GetTotalAddictionPenalty() != 0)
            {
                TryAdd(item, added, ItemFeatureList.AddictionSellPenalty());
            }
            if (StartingPerk.IsAwfulNegotiator())
            {
                TryAdd(item, added, ItemFeatureList.AwfulNegotiatorSellPenalty());
            }
            if (client.clientFaction == "FACTION_UPPER_LEVEL" && StoreReputation.IsPerkUnlocked("UL_TIPS"))
            {
                TryAdd(item, added, ItemFeatureList.Pourboire());
            }
        }

        private static void TryAdd(GameItem item, List<ItemFeature> added, ItemFeature feature)
        {
            if (feature == null || item == null)
            {
                return;
            }
            if (item.IsAlreadyContainFeatureWithId(feature.identifier))
            {
                return; // 已经有（重复灌装、或瓶子本来就是我卖出去的），别动它
            }
            item.AddItemFeature(feature);
            added?.Add(feature);
        }

        private static void ReleaseSellingFeatures(GameItem item, List<ItemFeature> added)
        {
            if (item == null || added == null)
            {
                return;
            }
            for (int i = added.Count - 1; i >= 0; i--)
            {
                try { item.RemoveItemFeature(added[i]); } catch { }
            }
            added.Clear();
        }

        /// <summary>
        /// tooltip 上补一行「价格」。只对「顾客摆在柜台上、还没买下的水容器 + 当前客人确实在买水」生效。
        /// 拖拽时的动作提示另有明细（<see cref="AppendPourBreakdown"/>）。
        /// </summary>
        internal static void AppendPourPriceTooltip(RichTextBuilder builder, GameItem item)
        {
            if (builder == null || !ModEntry.PourEnabled || !WaterPour.IsClientContainerOnCounter(item))
            {
                return;
            }
            PlayerStore store = PlayerStore.Instance;
            if (store == null || store.currentClientInstance == null)
            {
                return;
            }
            StoreClient client = store.currentClientInstance.GetClientBlueprint();
            if (client == null || client.clientBuyingTagList == null || !client.clientBuyingTagList.Contains("WATER"))
            {
                return;
            }

            string line = BuildPourLine(item);
            if (!string.IsNullOrEmpty(line))
            {
                builder.AddLine(line, ignoreEmpty: true, RenderHandler.ColorPalette.LightTurquoise);
            }
        }
    }
}
