namespace TDownloader.Core.Util;

/// <summary>
/// Kayan pencere hız ölçer: (zaman, toplam bayt) örneklerini sabit boyutlu halka tamponda tutar, hız = pencere
/// başı ile sonu arasındaki bayt farkı / süre. Pencere (varsayılan 16 örnek ~ 4 sn @250 ms) dışındaki örnekler atılır.
/// İş parçacığı güvenli değildir; çağıran kilitlemelidir.
/// </summary>
public sealed class SlidingSpeed
{
    private readonly double[] _t;
    private readonly long[] _b;
    private readonly double _windowSec;
    private int _head, _count;

    public SlidingSpeed(int capacity = 16, double windowSeconds = 4.0)
    {
        _t = new double[Math.Max(2, capacity)];
        _b = new long[_t.Length];
        _windowSec = windowSeconds;
    }

    /// <summary>Yeni örnek ekler ve yumuşatılmış hızı (bayt/sn) döndürür.</summary>
    public double Add(double seconds, long totalBytes)
    {
        if (_count == _t.Length) { _head = (_head + 1) % _t.Length; _count--; }
        int idx = (_head + _count) % _t.Length;
        _t[idx] = seconds; _b[idx] = totalBytes; _count++;
        while (_count > 2 && seconds - _t[_head] > _windowSec) { _head = (_head + 1) % _t.Length; _count--; }
        return Speed;
    }

    public double Speed
    {
        get
        {
            if (_count < 2) return 0;
            int last = (_head + _count - 1) % _t.Length;
            double dt = _t[last] - _t[_head];
            return dt <= 0.001 ? 0 : Math.Max(0, (_b[last] - _b[_head]) / dt);
        }
    }

    public void Reset() { _head = 0; _count = 0; }
}
