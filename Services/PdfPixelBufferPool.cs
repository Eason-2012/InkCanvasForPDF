namespace InkCanvasForPDF.Services;

/// <summary>页面渲染输出像素缓冲复用池（借鉴 KillerPDF PdfEncodingBufferPool）：
/// 每次渲染都新建 byte[]（1080p 一页 ≈20MB）会持续产生大对象堆垃圾，改为顺序渲染间共享一块备用缓冲。
/// 只保留一块最大的备用块，超过单块上限直接丢弃，避免池本身变成新占用。</summary>
internal static class PdfPixelBufferPool
{
    private const long MaximumRetainedBytes = 64L * 1024 * 1024;

    private static readonly object Sync = new();
    private static byte[]? _spare;

    /// <summary>取一块至少 minimumLength 字节的缓冲：命中备用块则取走，否则新建。
    /// 用 <see cref="System.GC.AllocateUninitializedArray{T}(int)"/> 跳过清零——调用方（PDFium）
    /// 会先 FillRect 铺白底再绘制，旧内容不会被读到。</summary>
    internal static byte[] Rent(int minimumLength)
    {
        if (minimumLength <= 0)
            return Array.Empty<byte>();
        lock (Sync)
        {
            var spare = _spare;
            _spare = null;
            if (spare is not null && spare.Length >= minimumLength)
                return spare;
        }
        return System.GC.AllocateUninitializedArray<byte>(minimumLength);
    }

    /// <summary>归还缓冲：只保留一块最大的备用块（单块上限 64MB，超出丢弃）。</summary>
    internal static void Return(byte[] buffer)
    {
        if (buffer.Length == 0 || buffer.Length > MaximumRetainedBytes)
            return;
        lock (Sync)
        {
            if (_spare is null || _spare.Length < buffer.Length)
                _spare = buffer;
        }
    }
}
