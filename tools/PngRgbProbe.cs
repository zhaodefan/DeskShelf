using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// 检查「全透明」PNG 的 RGB 通道是否仍然保留。
/// 若 RGB 完好，只需把 alpha 置 255 就能救回图片；若 RGB 也被清零则无法恢复。
/// 保持 C# 5 语法（PS 5.1 的 Add-Type 限制）。
/// </summary>
public static class PngRgbProbe
{
    /// <summary>报告尺寸、alpha 范围、RGB 是否非零、颜色种类数。</summary>
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
        if (src.Format != PixelFormats.Bgra32)
        {
            src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        }

        int w = src.PixelWidth;
        int h = src.PixelHeight;
        int stride = w * 4;
        byte[] px = new byte[(long)stride * h];
        src.CopyPixels(px, stride, 0);

        int maxA = 0;
        int maxR = 0;
        int maxG = 0;
        int maxB = 0;
        long nonBlack = 0;
        for (long i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] > maxA) { maxA = px[i + 3]; }
            if (px[i + 2] > maxR) { maxR = px[i + 2]; }
            if (px[i + 1] > maxG) { maxG = px[i + 1]; }
            if (px[i] > maxB) { maxB = px[i]; }
            if (px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0) { nonBlack++; }
        }

        long total = (long)w * h;
        double pct = total == 0 ? 0 : (100.0 * nonBlack / total);

        return w + "x" + h
            + " maxAlpha=" + maxA
            + " RGB峰值=(" + maxR + "," + maxG + "," + maxB + ")"
            + " 非黑像素=" + pct.ToString("F1") + "%"
            + (maxR + maxG + maxB > 0 ? "  → RGB 完好，可救回" : "  → RGB 也丢了，无法恢复");
    }

    /// <summary>把 alpha 全 0 的 PNG 修复为不透明，另存到 outputPath。</summary>
    public static string Repair(string path, string outputPath)
    {
        BitmapFrame frame;
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            frame = BitmapFrame.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }

        BitmapSource src = frame;
        if (src.Format != PixelFormats.Bgra32)
        {
            src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        }

        int w = src.PixelWidth;
        int h = src.PixelHeight;
        int stride = w * 4;
        byte[] px = new byte[(long)stride * h];
        src.CopyPixels(px, stride, 0);

        for (long i = 3; i < px.Length; i += 4)
        {
            px[i] = 255;
        }

        BitmapSource fixedSrc = BitmapSource.Create(w, h, src.DpiX, src.DpiY, PixelFormats.Bgra32, null, px, stride);
        PngBitmapEncoder enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(fixedSrc));
        using (FileStream outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
        {
            enc.Save(outFs);
        }

        return "已修复 → " + outputPath + " (" + new FileInfo(outputPath).Length + " 字节)";
    }
}
