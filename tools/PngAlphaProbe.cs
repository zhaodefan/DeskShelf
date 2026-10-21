using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// 统计 PNG 的 alpha 分布，用于判断图片是否「全透明」。
/// 注意：需在 STA 线程使用；本文件保持 C# 5 语法（PS 5.1 的 Add-Type 限制）。
/// </summary>
public static class PngAlphaProbe
{
    /// <summary>返回 "宽x高 格式 alpha=最小~最大 全透明像素占比" 形式的报告。</summary>
    public static string Inspect(string path)
    {
        if (!File.Exists(path))
        {
            return "文件不存在";
        }

        BitmapFrame frame;
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            frame = BitmapFrame.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }

        BitmapSource src = frame;
        if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32)
        {
            src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        }

        int w = src.PixelWidth;
        int h = src.PixelHeight;
        int stride = w * 4;
        byte[] pixels = new byte[(long)stride * h];
        src.CopyPixels(pixels, stride, 0);

        int minA = 255;
        int maxA = 0;
        long transparent = 0;
        long total = (long)w * h;

        for (long i = 3; i < pixels.Length; i += 4)
        {
            byte a = pixels[i];
            if (a < minA)
            {
                minA = a;
            }

            if (a > maxA)
            {
                maxA = a;
            }

            if (a == 0)
            {
                transparent++;
            }
        }

        double pct = total == 0 ? 0 : (100.0 * transparent / total);

        string verdict;
        if (maxA == 0)
        {
            verdict = "【全透明 —— 这就是问题图】";
        }
        else if (minA == 255)
        {
            verdict = "【全不透明，正常】";
        }
        else
        {
            verdict = "【部分透明】";
        }

        return w + "x" + h + " " + src.Format
            + " alpha=" + minA + "~" + maxA
            + " 透明像素=" + pct.ToString("F1") + "%  "
            + verdict;
    }
}
