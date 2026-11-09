using System.Net;
using System.Net.Sockets;
using TDownloader.Core.Queue;
using TDownloader.Core.Util;
using TDownloader.Core.Video;

namespace TDownloader.Core.Tests;

public class ProgressSmoothnessTests
{
    [Fact]
    public void SlidingSpeed_ReducesVarianceVersusInstantaneous()
    {
        // Gerçek hız 1000 B/s; 250 ms örnekler dalgalı (0.25 ile 1.75 kat arası).
        var win = new SlidingSpeed(16, 4.0);
        var rnd = new Random(7);
        long total = 0; double t = 0;
        var inst = new List<double>(); var smooth = new List<double>();
        for (int i = 0; i < 80; i++)
        {
            long add = (long)(250 * (0.25 + rnd.NextDouble() * 1.5));   // ort. 250 bayt/250 ms
            t += 0.25; total += add;
            double s = win.Add(t, total);
            if (i >= 16) { inst.Add(add / 0.25); smooth.Add(s); }
        }
        static double Sd(List<double> v) { double m = v.Average(); return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count); }
        Assert.True(Sd(smooth) < Sd(inst) / 3, $"smooth {Sd(smooth):F1} inst {Sd(inst):F1}");
        Assert.InRange(smooth.Average(), 800, 1200);
    }

    [Fact]
    public void SlidingSpeed_DropsSamplesOlderThanWindow()
    {
        var win = new SlidingSpeed(16, 4.0);
        win.Add(0, 0); win.Add(1, 10_000);                 // eski hızlı dönem
        for (int i = 2; i <= 12; i++) win.Add(i, 10_000);  // sonra durdu
        Assert.True(win.Speed < 100, win.Speed.ToString());
    }

    [Fact]
    public void YtDlpAggregator_TwoStreams_DoesNotResetPercent()
    {
        var agg = new YtDlpProgressAggregator(YtDlpProgressAggregator.ExpectedStreams("137+140"));
        Assert.Equal(2, YtDlpProgressAggregator.ExpectedStreams("bv*+ba/b"));
        var seq = new List<ExternalProgress>();
        double t = 0;
        void Feed(string line) { t += 0.25; seq.Add(agg.Add(YtDlpParsing.ParseProgressLine(line)!, t)); }
        string L(string st, long dl, long tot, string id) => $"{YtDlpParsing.ProgressPrefix}{st}|{dl}|{tot}|NA|1000|NA|{id}";
        for (long d = 0; d <= 1000; d += 250) Feed(L(d == 1000 ? "finished" : "downloading", d, 1000, "137"));
        for (long d = 0; d <= 100; d += 25) Feed(L(d == 100 ? "finished" : "downloading", d, 100, "140"));
        for (int i = 1; i < seq.Count; i++) Assert.True(seq[i].Downloaded >= seq[i - 1].Downloaded, $"geri gitti @{i}");
        Assert.Equal(1100, seq[^1].Downloaded);
        Assert.Equal(1100, seq[^1].Total);
        // Yüzde ikinci akışa geçerken ~%0'a düşmedi
        double pctAtSecondStart = seq[5].Downloaded * 100.0 / seq[5].Total;
        Assert.True(pctAtSecondStart > 80, pctAtSecondStart.ToString());
    }

    [Fact]
    public void YtDlpAggregator_WithoutStreamId_DetectsNewStreamAfterFinished()
    {
        var agg = new YtDlpProgressAggregator(2);
        var a = agg.Add(new YtDlpProgress("downloading", 500, 1000, 0, null), 0.25);
        agg.Add(new YtDlpProgress("finished", 1000, 1000, 0, null), 0.5);
        var b = agg.Add(new YtDlpProgress("downloading", 10, 100, 0, null), 0.75);
        Assert.True(b.Downloaded >= 1010);
        Assert.True(b.Downloaded > a.Downloaded);
    }

    [Fact]
    public void BuildDownloadArgs_ProgressDelta_OnlyWhenSupported()
    {
        var off = YtDlpClient.BuildDownloadArgs("https://x.test/v", "best", "o", null, null);
        Assert.DoesNotContain("--progress-delta", off);
        var on = YtDlpClient.BuildDownloadArgs("https://x.test/v", "best", "o", null, null, null, progressDelta: true);
        int i = on.IndexOf("--progress-delta");
        Assert.True(i >= 0); Assert.Equal("0.25", on[i + 1]);
    }

    [Fact]
    public async Task Hls_SingleBigSlowSegment_ReportsIntermediateProgress_NeverGoesBack()
    {
        const int packets = 2000;                       // ~376 KB
        var seg = HlsTestServer.Ts(packets, 3);
        var l = new HttpListener();
        int port;
        for (int attempt = 0; ; attempt++)
        {
            port = TestPorts.Next();
            l.Prefixes.Clear(); l.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { l.Start(); break; } catch (HttpListenerException) when (attempt < 20) { }
        }
        var serve = Task.Run(async () =>
        {
            while (true)
            {
                HttpListenerContext c;
                try { c = await l.GetContextAsync(); } catch { break; }
                try
                {
                    if (c.Request.Url!.AbsolutePath.EndsWith(".m3u8"))
                    {
                        var b = System.Text.Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\nbig.ts\n#EXT-X-ENDLIST\n");
                        c.Response.ContentType = "application/vnd.apple.mpegurl"; c.Response.ContentLength64 = b.Length;
                        c.Response.OutputStream.Write(b); c.Response.Close();
                    }
                    else
                    {
                        c.Response.ContentType = "video/mp2t"; c.Response.ContentLength64 = seg.Length;
                        int chunk = seg.Length / 10;
                        for (int o = 0; o < seg.Length; o += chunk)
                        {
                            c.Response.OutputStream.Write(seg, o, Math.Min(chunk, seg.Length - o));
                            c.Response.OutputStream.Flush();
                            await Task.Delay(120);
                        }
                        c.Response.Close();
                    }
                }
                catch { try { c.Response.Abort(); } catch { } }
            }
        });
        var tmp = Path.Combine(Path.GetTempPath(), "tdl-smooth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var prog = new List<ExternalProgress>();
            var path = await new HlsClient().DownloadAsync($"http://127.0.0.1:{port}/v/index.m3u8", null, new MediaContext(), Path.Combine(tmp, "o"),
                new HlsClient.Options(null, Parallel: 1), p => { lock (prog) prog.Add(p); }, CancellationToken.None);
            Assert.Equal(seg, File.ReadAllBytes(path));
            lock (prog)
            {
                var dl = prog.Where(p => p.Phase == null).Select(p => p.Downloaded).ToList();
                Assert.True(dl.Count(x => x > 0 && x < seg.Length) >= 3, "ara ilerleme olayı sayısı: " + dl.Count(x => x > 0 && x < seg.Length));
                for (int i = 1; i < dl.Count; i++) Assert.True(dl[i] >= dl[i - 1], "ilerleme geri gitti");
                Assert.Equal(seg.Length, dl[^1]);
            }
        }
        finally
        {
            try { l.Close(); } catch (ObjectDisposedException) { }
            try { await serve.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }   // dinleyici ömrü test boyunca: sunucu görevi bitmeden test bitmez
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}
