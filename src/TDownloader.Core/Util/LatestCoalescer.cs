namespace TDownloader.Core.Util;

/// <summary>
/// Anahtar başına yalnızca EN SON değeri tutar. Üretici hızlı, tüketici (UI thread) yavaş olduğunda
/// bekleyen işlerin sınırsız birikmesini önler: Offer true dönerse çağıran tek bir "boşalt" işi planlamalıdır;
/// false dönerse zaten planlı bir boşaltma vardır ve yeni değer onun göreceği yerdedir.
/// </summary>
public sealed class LatestCoalescer<TKey, TValue> where TKey : notnull
{
    private readonly object _lock = new();
    private readonly Dictionary<TKey, TValue> _latest = new();
    private readonly List<TKey> _order = new();
    private bool _scheduled;

    /// <summary>Değeri kaydeder. true = çağıran Drain'i planlamalı.</summary>
    public bool Offer(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (!_latest.ContainsKey(key)) _order.Add(key);
            _latest[key] = value;
            if (_scheduled) return false;
            _scheduled = true;
            return true;
        }
    }

    /// <summary>Bekleyen en son değerleri (ilk geliş sırasıyla) alır ve sıfırlar.</summary>
    public List<TValue> Drain()
    {
        lock (_lock)
        {
            var list = new List<TValue>(_order.Count);
            foreach (var k in _order) list.Add(_latest[k]);
            _order.Clear(); _latest.Clear();
            _scheduled = false;
            return list;
        }
    }

    public int Pending { get { lock (_lock) return _latest.Count; } }
}
