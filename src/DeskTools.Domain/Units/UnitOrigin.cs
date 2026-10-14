namespace DeskTools.Domain.Units;

/// <summary>unit 的来源，用于统计与「同源内容去重」等后续策略。</summary>
public enum UnitOrigin
{
    /// <summary>未知（反序列化兜底）。</summary>
    Unknown = 0,

    /// <summary>面板内 Ctrl+V 从剪贴板粘贴。</summary>
    Clipboard = 1,

    /// <summary>从外部拖拽落入面板。</summary>
    DragDrop = 2,
}
