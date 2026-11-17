using System;
using HarmonyLib;

namespace SmarterTrading
{
    /// <summary>
    /// 记录「本次来访里玩家卖给这位顾客的每件物品的标准成交价」，用来算均价。
    ///
    /// 为什么不在生成台词时现算：<c>OnDealAccepted</c> 触发时，刚成交的物品已经被
    /// <c>SoldItem.TrackItem</c> 搬去 soldElement，柜台上的可卖物品已经空了，
    /// 现算均价只会得到 0。所以在 <c>PlayerStore.SellItem</c> 的 Prefix 里记账——
    /// 那时 <c>GetNegociatedValue()</c> 还没被 SetItemOwned(false) 影响，是标准价。
    /// </summary>
    internal static class SaleHistory
    {
        private static StoreClient _client;
        private static long _sum;
        private static int _count;

        internal static void Record(StoreClient client, long value)
        {
            if (client == null)
            {
                return;
            }
            if (!ReferenceEquals(client, _client))
            {
                _client = client;
                _sum = 0;
                _count = 0;
            }
            if (value <= 0)
            {
                return;
            }
            _sum += value;
            _count++;
        }

        /// <summary>本次来访的成交均价；还没成交过则返回 0。</summary>
        internal static double Average(StoreClient client)
        {
            if (client == null || !ReferenceEquals(client, _client) || _count <= 0)
            {
                return 0.0;
            }
            return (double)_sum / _count;
        }

        internal static void Reset()
        {
            _client = null;
            _sum = 0;
            _count = 0;
        }
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.SellItem))]
    internal static class SellItemRecordPatch
    {
        private static void Prefix(GameItem item)
        {
            try
            {
                if (item == null || !PlayerStore.HasInstance)
                {
                    return;
                }
                StoreClientInstance instance = PlayerStore.Instance.currentClientInstance;
                if (instance == null)
                {
                    return;
                }
                StoreClient client = instance.GetClientBlueprint();
                if (client == null)
                {
                    return;
                }
                long value = item.GetNegociatedValue();
                SaleHistory.Record(client, value);
                if (ModEntry.Verbose)
                {
                    ModEntry.Log($"记一笔成交价：{item.GetDisplayName()} = {value}（均价基准）");
                }
            }
            catch (Exception e)
            {
                ModEntry.Warn("记录成交价失败: " + e.Message);
            }
        }
    }
}
