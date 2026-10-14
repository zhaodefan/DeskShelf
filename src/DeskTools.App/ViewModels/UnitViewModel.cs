using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskTools.Application.Catalog;
using DeskTools.Application.Services;
using DeskTools.App.Services;
using DeskTools.Domain.Units;
using DeskTools.Domain.ValueObjects;

namespace DeskTools.App.ViewModels;

/// <summary>
/// 单个 unit 卡片的 ViewModel。
/// 说明：三类 unit 共用本类而不是各写一个子类 —— 视图已经由
/// <c>UnitTemplateSelector</c> 按 <see cref="Kind"/> 分派到不同 DataTemplate，
/// 因此这里只需暴露各类型用得到的属性（不适用的保持 <c>null</c>），
/// 新增 unit 类型时补一个属性 + 一个 DataTemplate 即可。
/// </summary>
public sealed partial class UnitViewModel : ObservableObject
{
    private readonly ShelfService _shelf;
    private readonly ImageBytesCache _imageCache;
    private readonly ThumbnailLoader? _thumbnailLoader;
    private readonly CancellationTokenSource _lifetime = new();

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isHovered;

    [ObservableProperty]
    private BitmapImage? _thumbnail;

    /// <summary>创建 unit 的 ViewModel。</summary>
    /// <param name="unit">领域对象。</param>
    /// <param name="shelf">逻辑入口。</param>
    /// <param name="imageCache">图片字节缓存。</param>
    /// <param name="thumbnailLoader">缩略图加载器；设计期（假数据）可为 <c>null</c>。</param>
    public UnitViewModel(ShelfUnit unit, ShelfService shelf, ImageBytesCache imageCache, ThumbnailLoader? thumbnailLoader = null)
    {
        Model = unit ?? throw new ArgumentNullException(nameof(unit));
        _shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
        _imageCache = imageCache ?? throw new ArgumentNullException(nameof(imageCache));
        _thumbnailLoader = thumbnailLoader;

        Presentation = UnitCatalog.For(unit);
        Capabilities = unit.Capabilities;

        switch (unit)
        {
            case TextUnit text:
                Text = text.Text;
                break;
            case FolderUnit folder:
                FolderPath = folder.Path;
                FolderName = folder.DisplayName;
                break;
            case ImageUnit image:
                ImageWidth = image.PixelWidth;
                ImageHeight = image.PixelHeight;
                ImageMeta = $"{image.PixelWidth}×{image.PixelHeight} · {image.FormatDisplay} · {FormatSize(image.SizeBytes)}";
                ImageKey = image.ImageKey;
                break;
        }
    }

    /// <summary>对应的领域对象。</summary>
    public ShelfUnit Model { get; }

    /// <summary>类型规格（图标、显示名等），来自 <see cref="UnitCatalog"/>。</summary>
    public UnitPresentation Presentation { get; }

    /// <summary>能力声明，视图据此决定按钮可用性，不做类型判断。</summary>
    public UnitCapabilities Capabilities { get; }

    /// <summary>unit 标识。</summary>
    public UnitId Id => Model.Id;

    /// <summary>unit 类型。</summary>
    public UnitKind Kind => Model.Kind;

    /// <summary>类型显示名（用于无障碍与提示）。</summary>
    public string KindName => Presentation.DisplayName;

    /// <summary>文本内容（仅文本 unit 非空）。</summary>
    public string? Text { get; }

    /// <summary>文件夹路径（仅文件夹 unit 非空）。</summary>
    public string? FolderPath { get; }

    /// <summary>文件夹名（卡片主标题）。</summary>
    public string? FolderName { get; }

    /// <summary>图片像素宽（用于占位，避免加载时布局跳动）。</summary>
    public int ImageWidth { get; }

    /// <summary>图片像素高。</summary>
    public int ImageHeight { get; }

    /// <summary>图片元信息行（尺寸 · 格式 · 体积）。</summary>
    public string? ImageMeta { get; }

    /// <summary>图片存储键（非图片 unit 为 <c>null</c>）。</summary>
    public StorageKey? ImageKey { get; }

    /// <summary>图片是否尚未加载出来（用于显示占位骨架）。</summary>
    public bool IsImagePending => Kind == UnitKind.Image && Thumbnail is null;

    /// <summary>卡片副标题：文件夹路径、图片元信息。</summary>
    public string? Subtitle => Kind switch
    {
        UnitKind.Folder => FolderPath,
        UnitKind.Image => ImageMeta,
        _ => null,
    };

    /// <summary>
    /// 文本卡片是否需要在悬停/选中时才允许与鼠标交互（未选中时不拦截滚动）。
    /// </summary>
    public bool SupportsInPlaceEdit => Capabilities.CanEditText;

    /// <summary>悬停小标是否可见（折叠与透明度动画由视图层完成）。</summary>
    public bool ShowHoverActions => Capabilities.HasAnyAction;

    /// <summary>切换选中状态。</summary>
    public void ToggleSelection() => IsSelected = !IsSelected;

    /// <summary>设置选中状态。</summary>
    public void SetSelected(bool selected) => IsSelected = selected;

    /// <summary>进入行内编辑（仅文本 unit 有意义）。</summary>
    public void BeginEdit()
    {
        if (Capabilities.CanEditText)
        {
            IsEditing = true;
        }
    }

    /// <summary>结束行内编辑。</summary>
    public void EndEdit() => IsEditing = false;

    /// <summary>异步加载图片展示数据；非图片 unit、已解码或设计期无加载器时直接返回。</summary>
    public async Task EnsureImageLoadedAsync()
    {
        if (ImageKey is not { } key || key.IsEmpty || Thumbnail is not null || _thumbnailLoader is null)
        {
            return;
        }

        // 预取字节，使后续滚动到该卡片时命中 LRU 缓存
        _ = _imageCache.GetAsync(key, _lifetime.Token);

        try
        {
            Thumbnail = await _thumbnailLoader.GetAsync(key, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        OnPropertyChanged(nameof(IsImagePending));
    }

    /// <summary>释放（面板关闭时取消未完成的图片加载）。</summary>
    public void Release()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>提交编辑后的文本。</summary>
    public bool CommitText(string text) => _shelf.UpdateText(Id, text);

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes / (1024d * 1024d):0.##} MB",
    };
}
