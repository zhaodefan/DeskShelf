using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// 复现 DeskShelf 的剪贴板图片读取路径：完全照抄
/// <c>DataObjectReader.TryReadImagePng</c> 的做法，并报告 alpha 分布，
/// 以确认「全透明」发生在哪一步。必须在 STA 线程运行。
/// 保持 C# 5 语法（PS 5.1 的 Add-Type 限制）。
/// </summary>
public static class ClipImageRepro
{
    private static string AlphaOf(BitmapSource src0)
    {
        BitmapSource src = src0;
        if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32)
        {
            return "格式=" + src.Format + "（无 alpha 通道）";
        }

        int w = src.PixelWidth;
        int h = src.PixelHeight;
        int stride = w * 4;
        byte[] pixels = new byte[(long)stride * h];
        src.CopyPixels(pixels, stride, 0);

        int minA = 255;
        int maxA = 0;
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
        }

        return "格式=" + src.Format + " alpha=" + minA + "~" + maxA
            + (maxA == 0 ? " 【全透明】" : (minA == 255 ? " 【全不透明】" : " 【部分透明】"));
    }

    /// <summary>照抄产品代码路径，逐步报告。</summary>
    public static string Run()
    {
        string report = "";
        IDataObject data = Clipboard.GetDataObject();
        if (data == null)
        {
            return "剪贴板为空";
        }

        report += "剪贴板格式: " + string.Join(", ", data.GetFormats()) + "\n";

        object raw = data.GetData(DataFormats.Bitmap);
        report += "GetData(DataFormats.Bitmap) 返回类型: "
            + (raw == null ? "(null)" : raw.GetType().FullName) + "\n";

        BitmapSource source = raw as BitmapSource;
        if (source == null)
        {
            report += "不是 BitmapSource，无法继续\n";
            return report;
        }

        report += "解码结果: " + source.PixelWidth + "x" + source.PixelHeight + "  " + AlphaOf(source) + "\n";

        // 照抄 TryReadImagePng 的编码过程
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        byte[] bytes;
        using (MemoryStream ms = new MemoryStream())
        {
            encoder.Save(ms);
            bytes = ms.ToArray();
        }

        report += "编码为 PNG: " + bytes.Length + " 字节\n";

        // 重新解码这些字节，看落盘后是什么样
        using (MemoryStream ms2 = new MemoryStream(bytes))
        {
            BitmapFrame back = BitmapFrame.Create(ms2, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            report += "重新解码 PNG: " + back.PixelWidth + "x" + back.PixelHeight + "  " + AlphaOf(back) + "\n";
        }

        return report;
    }
}
