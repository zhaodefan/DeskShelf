using System;
using System.Runtime.InteropServices;

/// <summary>
/// CF_DIB / CF_DIBV5 头解析：判断位深、压缩方式、alpha 掩码，并抽样统计 alpha 字节。
/// 用来确认「截图工具放进剪贴板的 32bpp DIB 是否把所有 alpha 写成 0」。
/// 注意：本文件要能被 Windows PowerShell 5.1 的 Add-Type 编译（C# 5），
/// 因此不能用表达式体成员、字符串插值等新语法。
/// </summary>
public static class DibProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    private static int I32(byte[] b, int o)
    {
        return BitConverter.ToInt32(b, o);
    }

    private static short I16(byte[] b, int o)
    {
        return BitConverter.ToInt16(b, o);
    }

    /// <summary>解析指定剪贴板 DIB 格式，返回可读报告。</summary>
    public static string Inspect(uint format)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return "OpenClipboard 失败";
        }

        try
        {
            IntPtr h = GetClipboardData(format);
            if (h == IntPtr.Zero)
            {
                return "GetClipboardData 返回空";
            }

            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero)
            {
                return "GlobalLock 失败";
            }

            try
            {
                // 先读出 header 的前 128 字节（足够覆盖 BITMAPV5HEADER）
                byte[] head = new byte[128];
                Marshal.Copy(p, head, 0, head.Length);

                int biSize = I32(head, 0);
                int width = I32(head, 4);
                int height = I32(head, 8);
                short planes = I16(head, 12);
                short bitCount = I16(head, 14);
                int compression = I32(head, 16);
                int sizeImage = I32(head, 20);
                int clrUsed = I32(head, 32);

                string report = "biSize=" + biSize
                    + " 尺寸=" + width + "x" + Math.Abs(height)
                    + " planes=" + planes + " bitCount=" + bitCount
                    + " compression=" + compression
                    + " sizeImage=" + sizeImage
                    + " clrUsed=" + clrUsed
                    + " 自顶向下=" + (height < 0);

                // BITMAPV4/V5HEADER 才有 alpha 掩码（bV4AlphaMask 偏移 52）
                if (biSize >= 108)
                {
                    uint alphaMask = BitConverter.ToUInt32(head, 52);
                    uint redMask = BitConverter.ToUInt32(head, 40);
                    uint csType = BitConverter.ToUInt32(head, 108 - 40);
                    report += "\n    V4/V5: redMask=0x" + redMask.ToString("X8")
                        + " alphaMask=0x" + alphaMask.ToString("X8")
                        + " csType=" + csType;
                }

                // 32bpp 才有 alpha 字节可看
                if (bitCount == 32)
                {
                    int paletteSize = clrUsed > 0 ? clrUsed * 4 : 0;
                    int pixelOffset = biSize + paletteSize;
                    int stride = width * 4;
                    int rows = Math.Abs(height);

                    int[] alphas = new int[256];
                    long sampled = 0;
                    int step = Math.Max(1, (width) / 40);   // 每行最多抽 40 个像素

                    byte[] buf = new byte[Math.Min(stride, 1 << 20)];
                    for (int y = 0; y < rows && y < 200; y++)
                    {
                        IntPtr rowStart = (IntPtr)((long)p + pixelOffset + ((long)y * stride));
                        int toRead = Math.Min(stride, buf.Length);
                        Marshal.Copy(rowStart, buf, 0, toRead);
                        for (int x = 0; x < toRead / 4; x += step)
                        {
                            alphas[buf[(x * 4) + 3]]++;
                            sampled++;
                        }
                    }

                    int minA = -1;
                    int maxA = -1;
                    for (int i = 0; i < 256; i++)
                    {
                        if (alphas[i] == 0)
                        {
                            continue;
                        }

                        if (minA < 0)
                        {
                            minA = i;
                        }

                        maxA = i;
                    }

                    report += "\n    抽样像素=" + sampled + "  alpha 范围=" + minA + "~" + maxA;
                    if (maxA == 0)
                    {
                        report += "  【全透明！所有 alpha=0】";
                    }
                    else if (minA == 255)
                    {
                        report += "  【全不透明，alpha 正确】";
                    }
                    else
                    {
                        report += "  【部分透明】";
                    }
                }
                else
                {
                    report += "  （非 32bpp，无 alpha 通道，不会全透明）";
                }

                return report;
            }
            finally
            {
                GlobalUnlock(h);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }
}
